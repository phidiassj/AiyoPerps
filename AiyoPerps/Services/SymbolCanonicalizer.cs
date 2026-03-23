using System;
using System.Linq;

namespace AiyoPerps.Services;

public readonly record struct CanonicalSymbolDescriptor(
    string RawSymbol,
    string CanonicalKey,
    string DisplaySymbol,
    string? BaseAsset,
    string? QuoteAsset,
    string? SettleAsset,
    string? ContractType);

public static class SymbolCanonicalizer
{
    private static readonly string[] KnownQuoteAssets =
    [
        "USDT",
        "USDC",
        "USD"
    ];

    public static CanonicalSymbolDescriptor Describe(
        string? venueId,
        string rawSymbol,
        string? baseAsset = null,
        string? quoteAsset = null,
        string? settleAsset = null,
        string? contractType = null)
    {
        var normalizedRaw = NormalizeToken(rawSymbol);
        if (string.IsNullOrWhiteSpace(normalizedRaw))
        {
            return new CanonicalSymbolDescriptor(string.Empty, "RAW:", string.Empty, null, null, null, null);
        }

        var normalizedVenue = NormalizeToken(venueId);
        string? normalizedBase = NormalizeAsset(baseAsset);
        string? normalizedQuote = NormalizeAsset(quoteAsset);
        string? normalizedSettle = NormalizeAsset(settleAsset);
        string? normalizedContract = NormalizeContractType(contractType);

        ApplyVenueDefaults(normalizedVenue, normalizedRaw, ref normalizedBase, ref normalizedQuote, ref normalizedSettle, ref normalizedContract);

        if (string.IsNullOrWhiteSpace(normalizedBase) || string.IsNullOrWhiteSpace(normalizedQuote))
        {
            var parsed = TryParse(normalizedVenue, normalizedRaw);
            if (string.IsNullOrWhiteSpace(normalizedBase))
            {
                normalizedBase = parsed.BaseAsset;
            }

            if (string.IsNullOrWhiteSpace(normalizedQuote))
            {
                normalizedQuote = parsed.QuoteAsset;
            }

            if (string.IsNullOrWhiteSpace(normalizedSettle))
            {
                normalizedSettle = parsed.SettleAsset;
            }

            if (string.IsNullOrWhiteSpace(normalizedContract))
            {
                normalizedContract = parsed.ContractType;
            }
        }

        normalizedSettle = string.IsNullOrWhiteSpace(normalizedSettle) ? normalizedQuote : normalizedSettle;
        var displaySymbol = BuildDisplaySymbol(normalizedRaw, normalizedBase, normalizedQuote, normalizedContract);
        var canonicalKey = BuildCanonicalKey(normalizedRaw, normalizedBase, normalizedQuote, normalizedSettle, normalizedContract);

        return new CanonicalSymbolDescriptor(
            normalizedRaw,
            canonicalKey,
            displaySymbol,
            normalizedBase,
            normalizedQuote,
            normalizedSettle,
            normalizedContract);
    }

    public static string Format(string symbol)
        => Describe(null, symbol).DisplaySymbol;

    public static string Format(string? venueId, string symbol)
        => Describe(venueId, symbol).DisplaySymbol;

    private static void ApplyVenueDefaults(
        string normalizedVenue,
        string normalizedRaw,
        ref string? baseAsset,
        ref string? quoteAsset,
        ref string? settleAsset,
        ref string? contractType)
    {
        if (!string.Equals(normalizedVenue, "HYPERLIQUID", StringComparison.Ordinal))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            baseAsset = NormalizeAsset(normalizedRaw);
        }

        if (string.IsNullOrWhiteSpace(quoteAsset))
        {
            quoteAsset = "USDC";
        }

        if (string.IsNullOrWhiteSpace(settleAsset))
        {
            settleAsset = "USDC";
        }

        if (string.IsNullOrWhiteSpace(contractType))
        {
            contractType = "PERP";
        }
    }

    private static (string? BaseAsset, string? QuoteAsset, string? SettleAsset, string? ContractType) TryParse(string normalizedVenue, string normalizedRaw)
    {
        if (string.IsNullOrWhiteSpace(normalizedRaw))
        {
            return default;
        }

        if (normalizedRaw.Contains('_', StringComparison.Ordinal))
        {
            var parts = normalizedRaw
                .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                return (
                    NormalizeAsset(parts[0]),
                    NormalizeAsset(parts[1]),
                    NormalizeAsset(parts[1]),
                    parts.Length >= 3 ? NormalizeContractType(parts[2]) : "PERP");
            }
        }

        if (normalizedRaw.Contains('-', StringComparison.Ordinal))
        {
            var parts = normalizedRaw
                .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                return (
                    NormalizeAsset(parts[0]),
                    NormalizeAsset(parts[1]),
                    NormalizeAsset(parts[1]),
                    parts.Length >= 3 ? NormalizeContractType(parts[2]) : "PERP");
            }
        }

        foreach (var quote in KnownQuoteAssets)
        {
            if (!normalizedRaw.EndsWith(quote, StringComparison.Ordinal) || normalizedRaw.Length <= quote.Length)
            {
                continue;
            }

            var basePart = normalizedRaw[..^quote.Length];
            if (!basePart.All(char.IsLetterOrDigit))
            {
                continue;
            }

            return (NormalizeAsset(basePart), quote, quote, "PERP");
        }

        if (string.Equals(normalizedVenue, "HYPERLIQUID", StringComparison.Ordinal) &&
            normalizedRaw.All(char.IsLetterOrDigit))
        {
            return (NormalizeAsset(normalizedRaw), "USDC", "USDC", "PERP");
        }

        return default;
    }

    private static string BuildDisplaySymbol(string normalizedRaw, string? baseAsset, string? quoteAsset, string? contractType)
    {
        if (string.IsNullOrWhiteSpace(baseAsset) || string.IsNullOrWhiteSpace(quoteAsset))
        {
            return normalizedRaw;
        }

        var display = $"{baseAsset}-{quoteAsset}";
        if (!string.IsNullOrWhiteSpace(contractType) &&
            !string.Equals(contractType, "PERP", StringComparison.Ordinal))
        {
            display += $" ({contractType})";
        }

        return display;
    }

    private static string BuildCanonicalKey(string normalizedRaw, string? baseAsset, string? quoteAsset, string? settleAsset, string? contractType)
    {
        if (string.IsNullOrWhiteSpace(baseAsset) || string.IsNullOrWhiteSpace(quoteAsset))
        {
            return $"RAW:{normalizedRaw}";
        }

        var normalizedSettle = string.IsNullOrWhiteSpace(settleAsset) ? quoteAsset : settleAsset;
        var normalizedContract = string.IsNullOrWhiteSpace(contractType) ? "UNKNOWN" : contractType;
        return $"{normalizedContract}:{baseAsset}:{quoteAsset}:{normalizedSettle}";
    }

    private static string? NormalizeAsset(string? value)
    {
        var token = NormalizeToken(value);
        return token switch
        {
            "" => null,
            "XBT" => "BTC",
            _ => token
        };
    }

    private static string? NormalizeContractType(string? value)
    {
        var token = NormalizeToken(value);
        return token switch
        {
            "" => null,
            "PERPETUAL" => "PERP",
            _ => token
        };
    }

    private static string NormalizeToken(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();
}
