using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps.Services;

public sealed class SymbolCatalogSyncService
{
    private sealed record HyperliquidPerpDex(string? Dex, IReadOnlyList<string> Symbols);

    private readonly SymbolCatalogRepository _repository;
    private readonly AppLogger _logger;
    private readonly HttpClient _http = new();

    public SymbolCatalogSyncService(SymbolCatalogRepository repository, AppLogger logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task SyncAllAsync(CancellationToken cancellationToken = default)
    {
        await SyncBitMexAsync("mainnet", cancellationToken);
        await SyncBitMexAsync("testnet", cancellationToken);
        await SyncHyperliquidAsync("mainnet", cancellationToken);
        await SyncHyperliquidAsync("testnet", cancellationToken);
        await SyncAsterAsync("mainnet", cancellationToken);
        await SyncAsterAsync("testnet", cancellationToken);
        await SyncGrvtAsync("mainnet", cancellationToken);
        await SyncGrvtAsync("testnet", cancellationToken);
        await SyncDydxAsync("mainnet", cancellationToken);
        await SyncDydxAsync("testnet", cancellationToken);
    }

    public async Task SyncBitMexAsync(string environment, CancellationToken cancellationToken = default)
    {
        var baseUrl = string.Equals(environment, "testnet", StringComparison.OrdinalIgnoreCase)
            ? "https://testnet.bitmex.com"
            : "https://www.bitmex.com";
        var url = $"{baseUrl}/api/v1/instrument/active?count=1000";

        try
        {
            _logger.Info("SymbolSync", $"BitMEX sync start env={environment}");
            using var resp = await _http.GetAsync(url, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Error("SymbolSync", $"BitMEX sync failed env={environment}, status={(int)resp.StatusCode}, body={Trim(body)}");
                return;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                _logger.Warn("SymbolSync", $"BitMEX sync unexpected payload env={environment}");
                return;
            }

            var symbols = new List<SymbolCatalogUpsert>();
            var skippedNoPrice = 0;
            var skippedQuote = 0;
            var skippedState = 0;
            var skippedSymbol = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("symbol", out var sym))
                {
                    continue;
                }

                var symbol = sym.GetString();
                if (string.IsNullOrWhiteSpace(symbol) || !IsValidBitMexSymbol(symbol))
                {
                    skippedSymbol++;
                    continue;
                }

                var state = ReadString(item, "state");
                if (!string.Equals(state, "Open", StringComparison.OrdinalIgnoreCase))
                {
                    skippedState++;
                    continue;
                }

                var quote = ReadString(item, "quoteCurrency")?.ToUpperInvariant() ?? string.Empty;
                if (!AllowedBitMexQuotes.Contains(quote))
                {
                    skippedQuote++;
                    continue;
                }

                var lastPrice = ReadDecimal(item, "lastPrice");
                var markPrice = ReadDecimal(item, "markPrice");
                if (lastPrice <= 0 && markPrice <= 0)
                {
                    skippedNoPrice++;
                    continue;
                }

                symbols.Add(SymbolCatalogUpsert.FromVenueSymbol(
                    "BitMEX",
                    symbol,
                    baseAsset: ReadString(item, "rootSymbol") ?? ReadString(item, "underlying"),
                    quoteAsset: quote,
                    settleAsset: ReadString(item, "settlCurrency") ?? ReadString(item, "settleCurrency"),
                    contractType: "PERP"));
            }

            var result = _repository.ReplaceSymbols("BitMEX", environment, symbols);
            _logger.Info("SymbolSync", $"BitMEX sync done env={environment}, total={result.Total}, added={result.Added}, removed={result.Removed}, skippedNoPrice={skippedNoPrice}, skippedQuote={skippedQuote}, skippedState={skippedState}, skippedSymbol={skippedSymbol}");
        }
        catch (Exception ex)
        {
            _logger.Error("SymbolSync", $"BitMEX sync exception env={environment}", ex);
        }
    }

    public async Task SyncHyperliquidAsync(string environment, CancellationToken cancellationToken = default)
    {
        var baseUrl = string.Equals(environment, "testnet", StringComparison.OrdinalIgnoreCase)
            ? "https://api.hyperliquid-testnet.xyz"
            : "https://api.hyperliquid.xyz";

        try
        {
            _logger.Info("SymbolSync", $"Hyperliquid sync start env={environment}");
            var dexes = await FetchHyperliquidPerpDexesAsync(baseUrl, cancellationToken);
            var mids = await FetchHyperliquidMidsAsync(baseUrl, dexes, cancellationToken);
            var symbols = new List<SymbolCatalogUpsert>();
            var skippedNoMid = 0;
            var skippedInvalid = 0;
            foreach (var dex in dexes)
            {
                foreach (var symbol in dex.Symbols)
                {
                    if (string.IsNullOrWhiteSpace(symbol) || !IsValidHyperliquidSymbol(symbol))
                    {
                        skippedInvalid++;
                        continue;
                    }

                    if (!mids.TryGetValue(symbol, out var mid) || mid <= 0)
                    {
                        skippedNoMid++;
                        continue;
                    }

                    symbols.Add(SymbolCatalogUpsert.FromVenueSymbol(
                        "Hyperliquid",
                        symbol,
                        baseAsset: NormalizeHyperliquidBaseAsset(symbol),
                        quoteAsset: "USDC",
                        settleAsset: "USDC",
                        contractType: "PERP"));
                }
            }

            var result = _repository.ReplaceSymbols("Hyperliquid", environment, symbols);
            _logger.Info("SymbolSync", $"Hyperliquid sync done env={environment}, total={result.Total}, added={result.Added}, removed={result.Removed}, skippedNoMid={skippedNoMid}, skippedInvalid={skippedInvalid}");
        }
        catch (Exception ex)
        {
            _logger.Error("SymbolSync", $"Hyperliquid sync exception env={environment}", ex);
        }
    }

    public async Task SyncAsterAsync(string environment, CancellationToken cancellationToken = default)
    {
        var baseUrl = string.Equals(environment, "testnet", StringComparison.OrdinalIgnoreCase)
            ? "https://fapi.asterdex-testnet.com"
            : "https://fapi.asterdex.com";
        var url = $"{baseUrl}/fapi/v3/exchangeInfo";

        try
        {
            _logger.Info("SymbolSync", $"Aster sync start env={environment}, url={url}");
            using var resp = await _http.GetAsync(url, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Error("SymbolSync", $"Aster sync failed env={environment}, status={(int)resp.StatusCode}, body={Trim(body)}");
                return;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("symbols", out var symbolsNode) || symbolsNode.ValueKind != JsonValueKind.Array)
            {
                _logger.Warn("SymbolSync", $"Aster sync unexpected payload env={environment}");
                return;
            }

            var symbols = new List<SymbolCatalogUpsert>();
            var skippedStatus = 0;
            var skippedType = 0;
            var skippedInvalid = 0;
            foreach (var item in symbolsNode.EnumerateArray())
            {
                var symbol = ReadString(item, "symbol");
                if (string.IsNullOrWhiteSpace(symbol) || !IsValidAsterSymbol(symbol))
                {
                    skippedInvalid++;
                    continue;
                }

                var status = ReadString(item, "status");
                if (!string.Equals(status, "TRADING", StringComparison.OrdinalIgnoreCase))
                {
                    skippedStatus++;
                    continue;
                }

                var contractType = ReadString(item, "contractType");
                if (!string.IsNullOrWhiteSpace(contractType) &&
                    !string.Equals(contractType, "PERPETUAL", StringComparison.OrdinalIgnoreCase))
                {
                    skippedType++;
                    continue;
                }

                symbols.Add(SymbolCatalogUpsert.FromVenueSymbol(
                    "Aster",
                    symbol,
                    baseAsset: ReadString(item, "baseAsset"),
                    quoteAsset: ReadString(item, "quoteAsset"),
                    settleAsset: ReadString(item, "marginAsset"),
                    contractType: contractType));
            }

            var result = _repository.ReplaceSymbols("Aster", environment, symbols);
            _logger.Info("SymbolSync", $"Aster sync done env={environment}, total={result.Total}, added={result.Added}, removed={result.Removed}, skippedStatus={skippedStatus}, skippedType={skippedType}, skippedInvalid={skippedInvalid}");
        }
        catch (Exception ex)
        {
            _logger.Error("SymbolSync", $"Aster sync exception env={environment}", ex);
        }
    }

    public async Task SyncGrvtAsync(string environment, CancellationToken cancellationToken = default)
    {
        var baseUrl = string.Equals(environment, "testnet", StringComparison.OrdinalIgnoreCase)
            ? "https://market-data.testnet.grvt.io"
            : "https://market-data.grvt.io";
        var url = $"{baseUrl}/full/v1/all_instruments";

        try
        {
            _logger.Info("SymbolSync", $"GRVT sync start env={environment}, url={url}");
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("{\"is_active\":true}", Encoding.UTF8, "application/json")
            };

            using var resp = await _http.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Error("SymbolSync", $"GRVT sync failed env={environment}, status={(int)resp.StatusCode}, body={Trim(body)}");
                return;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var result))
            {
                root = result;
            }

            JsonElement list;
            if (root.ValueKind == JsonValueKind.Array)
            {
                list = root;
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("instruments", out var instruments) && instruments.ValueKind == JsonValueKind.Array)
            {
                list = instruments;
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                list = items;
            }
            else
            {
                _logger.Warn("SymbolSync", $"GRVT sync unexpected payload env={environment}");
                return;
            }

            var symbols = new List<SymbolCatalogUpsert>();
            var skippedInvalid = 0;
            var skippedInactive = 0;
            foreach (var item in list.EnumerateArray())
            {
                var symbol = (ReadString(item, "instrument") ?? ReadString(item, "symbol") ?? string.Empty).ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(symbol) || !IsValidGrvtSymbol(symbol))
                {
                    skippedInvalid++;
                    continue;
                }

                var active = ReadString(item, "status");
                if (!string.IsNullOrWhiteSpace(active) &&
                    !active.Equals("TRADING", StringComparison.OrdinalIgnoreCase) &&
                    !active.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase))
                {
                    skippedInactive++;
                    continue;
                }

                symbols.Add(SymbolCatalogUpsert.FromVenueSymbol(
                    "GRVT",
                    symbol,
                    baseAsset: ReadString(item, "base_asset") ?? ReadString(item, "baseAsset") ?? ReadString(item, "underlying_asset") ?? ReadString(item, "underlyingAsset"),
                    quoteAsset: ReadString(item, "quote_asset") ?? ReadString(item, "quoteAsset"),
                    settleAsset: ReadString(item, "settle_asset") ?? ReadString(item, "settleAsset"),
                    contractType: ReadString(item, "contract_type") ?? ReadString(item, "contractType") ?? ReadString(item, "instrument_type") ?? ReadString(item, "instrumentType") ?? "PERP"));
            }

            var unique = symbols
                .GroupBy(x => x.RawSymbol, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
            var replace = _repository.ReplaceSymbols("GRVT", environment, unique);
            _logger.Info("SymbolSync", $"GRVT sync done env={environment}, total={replace.Total}, added={replace.Added}, removed={replace.Removed}, skippedInvalid={skippedInvalid}, skippedInactive={skippedInactive}");
        }
        catch (Exception ex)
        {
            _logger.Error("SymbolSync", $"GRVT sync exception env={environment}", ex);
        }
    }

    public async Task SyncDydxAsync(string environment, CancellationToken cancellationToken = default)
    {
        var baseUrl = string.Equals(environment, "testnet", StringComparison.OrdinalIgnoreCase)
            ? "https://indexer.v4testnet.dydx.exchange"
            : "https://indexer.dydx.trade";
        var url = $"{baseUrl}/v4/perpetualMarkets";

        try
        {
            _logger.Info("SymbolSync", $"dYdX sync start env={environment}, url={url}");
            using var resp = await _http.GetAsync(url, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Error("SymbolSync", $"dYdX sync failed env={environment}, status={(int)resp.StatusCode}, body={Trim(body)}");
                return;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("markets", out var marketsNode) || marketsNode.ValueKind != JsonValueKind.Object)
            {
                _logger.Warn("SymbolSync", $"dYdX sync unexpected payload env={environment}");
                return;
            }

            var symbols = new List<SymbolCatalogUpsert>();
            var skippedInactive = 0;
            var skippedIsolated = 0;
            var skippedInvalid = 0;

            foreach (var market in marketsNode.EnumerateObject())
            {
                var ticker = market.Name?.Trim().ToUpperInvariant() ?? string.Empty;
                if (!IsValidDydxSymbol(ticker))
                {
                    skippedInvalid++;
                    continue;
                }

                var status = ReadString(market.Value, "status");
                if (!string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                {
                    skippedInactive++;
                    continue;
                }

                var marketType = ReadString(market.Value, "marketType");
                if (!string.Equals(marketType, "CROSS", StringComparison.OrdinalIgnoreCase))
                {
                    skippedIsolated++;
                    continue;
                }

                symbols.Add(SymbolCatalogUpsert.FromVenueSymbol(
                    "dYdX",
                    ticker,
                    contractType: "PERP"));
            }

            var result = _repository.ReplaceSymbols("dYdX", environment, symbols);
            _logger.Info("SymbolSync", $"dYdX sync done env={environment}, total={result.Total}, added={result.Added}, removed={result.Removed}, skippedInactive={skippedInactive}, skippedIsolated={skippedIsolated}, skippedInvalid={skippedInvalid}");
        }
        catch (Exception ex)
        {
            _logger.Error("SymbolSync", $"dYdX sync exception env={environment}", ex);
        }
    }

    private async Task<IReadOnlyList<HyperliquidPerpDex>> FetchHyperliquidPerpDexesAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}/info";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { type = "allPerpMetas" }), Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.Warn("SymbolSync", $"Hyperliquid allPerpMetas failed status={(int)resp.StatusCode}, body={Trim(body)}");
            return [];
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var dexes = new List<HyperliquidPerpDex>();
        foreach (var dexElement in doc.RootElement.EnumerateArray())
        {
            if (!dexElement.TryGetProperty("universe", out var universe) || universe.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var symbols = universe.EnumerateArray()
                .Select(item => ReadString(item, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .ToList();

            if (symbols.Count == 0)
            {
                continue;
            }

            dexes.Add(new HyperliquidPerpDex(InferHyperliquidDexName(symbols), symbols));
        }

        return dexes;
    }

    private async Task<Dictionary<string, decimal>> FetchHyperliquidMidsAsync(
        string baseUrl,
        IReadOnlyList<HyperliquidPerpDex> dexes,
        CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}/info";
        var mids = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var dex in dexes)
        {
            object payload = string.IsNullOrWhiteSpace(dex.Dex)
                ? new { type = "allMids" }
                : new { type = "allMids", dex = dex.Dex };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            using var resp = await _http.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Warn("SymbolSync", $"Hyperliquid allMids failed dex={dex.Dex ?? "default"} status={(int)resp.StatusCode}, body={Trim(body)}");
                continue;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String &&
                    decimal.TryParse(prop.Value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) &&
                    parsed > 0)
                {
                    mids[prop.Name.Trim()] = parsed;
                    continue;
                }

                if (prop.Value.ValueKind == JsonValueKind.Number &&
                    prop.Value.TryGetDecimal(out var numeric) &&
                    numeric > 0)
                {
                    mids[prop.Name.Trim()] = numeric;
                }
            }
        }

        return mids;
    }

    private static string? InferHyperliquidDexName(IReadOnlyList<string> symbols)
    {
        foreach (var symbol in symbols)
        {
            var colonIndex = symbol.IndexOf(':');
            if (colonIndex > 0)
            {
                return symbol[..colonIndex].Trim().ToLowerInvariant();
            }

            return null;
        }

        return null;
    }

    private static string NormalizeHyperliquidBaseAsset(string symbol)
    {
        var token = symbol.Trim().ToUpperInvariant();
        var colonIndex = token.LastIndexOf(':');
        if (colonIndex >= 0 && colonIndex < token.Length - 1)
        {
            token = token[(colonIndex + 1)..];
        }

        return token == "XBT" ? "BTC" : token;
    }

    private static readonly HashSet<string> AllowedBitMexQuotes =
    [
        "USD",
        "USDT",
        "USDC"
    ];

    private static bool IsValidBitMexSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var s = symbol.Trim().ToUpperInvariant();
        if (s.Length < 3 || s.Length > 32)
        {
            return false;
        }

        if (s.StartsWith(".", StringComparison.Ordinal) || s.Contains(':'))
        {
            return false;
        }

        return true;
    }

    private static bool IsValidHyperliquidSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var s = symbol.Trim().ToUpperInvariant();
        if (s.Length < 1 || s.Length > 48)
        {
            return false;
        }

        if (s.IndexOf(':') < 0)
        {
            return s.All(ch => char.IsLetterOrDigit(ch));
        }

        var parts = s.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts.All(part => part.Length >= 1 && part.All(char.IsLetterOrDigit));
    }

    private static bool IsValidAsterSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var s = symbol.Trim().ToUpperInvariant();
        if (s.Length < 6 || s.Length > 32)
        {
            return false;
        }

        return s.All(ch => char.IsLetterOrDigit(ch));
    }

    private static bool IsValidGrvtSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var s = symbol.Trim().ToUpperInvariant();
        if (s.Length < 6 || s.Length > 64)
        {
            return false;
        }

        return s.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-');
    }

    private static bool IsValidDydxSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var s = symbol.Trim().ToUpperInvariant();
        if (s.Length < 5 || s.Length > 96)
        {
            return false;
        }

        return s.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ',' or '.');
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            _ => prop.GetRawText()
        };
    }

    private static decimal ReadDecimal(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return 0m;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var d))
        {
            return d;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out d))
        {
            return d;
        }

        return 0m;
    }

    private static string Trim(string text)
    {
        return text.Length > 240 ? text[..240] : text;
    }
}
