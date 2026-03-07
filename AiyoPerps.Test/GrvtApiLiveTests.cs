using AiyoPerps.Core;
using AiyoPerps.Models;
using AiyoPerps.Services;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AiyoPerps.Test;

public sealed class GrvtApiLiveTests
{
    private static readonly string DbPath = Environment.GetEnvironmentVariable("AIYOPERPS_DB_PATH")
        ?? "/mnt/e/work/AiyoPerps/AiyoPerps/AiyoPerps/bin/Debug/net10.0/db/AiyoPerps.main.db";

    [Fact]
    public async Task ValidateConnection_TestnetGrvt_FromStoredApiKey_ShouldSucceed()
    {
        if (!ShouldRunLiveTests())
        {
            return;
        }

        var (environment, credentials) = LoadGrvtCredentialsFromDb(DbPath);
        var logger = new AppLogger();
        await using var venue = new GrvtVenueAdapter(environment, credentials, logger);

        var result = await venue.ValidateConnectionAsync();

        Assert.True(result.IsSuccess, $"GRVT ValidateConnection failed: {result.Message}");
    }

    [Fact]
    public async Task GetRecentCandles_TestnetGrvt_ShouldReturnRows()
    {
        if (!ShouldRunLiveTests())
        {
            return;
        }

        var (environment, credentials) = LoadGrvtCredentialsFromDb(DbPath);
        var logger = new AppLogger();
        await using var venue = new GrvtVenueAdapter(environment, credentials, logger);

        var candles = await venue.GetRecentCandlesAsync("BTC_USDT_Perp", CandleInterval.M5, 120);

        Assert.NotNull(candles);
        Assert.True(candles.Count > 10, $"Expected >10 candles, got {candles.Count}.");
    }

    [Fact]
    public async Task ConnectMarketData_TestnetGrvt_ShouldReceiveTradeTick()
    {
        if (!ShouldRunLiveTests())
        {
            return;
        }

        var (environment, credentials) = LoadGrvtCredentialsFromDb(DbPath);
        var logger = new AppLogger();
        await using var venue = new GrvtVenueAdapter(environment, credentials, logger);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        await venue.ConnectMarketDataAsync(["BTC_USDT_Perp"], cts.Token);
        try
        {
            await foreach (var evt in venue.MarketEvents(cts.Token))
            {
                if (evt is TradeTick tick && tick.Price > 0 && tick.Size > 0)
                {
                    return;
                }
            }
        }
        finally
        {
            await venue.DisconnectMarketDataAsync();
        }

        Assert.Fail("Did not receive GRVT trade tick within timeout.");
    }

    [Fact]
    public async Task GetAccountSnapshot_TestnetGrvt_ShouldReturnBalances()
    {
        if (!ShouldRunLiveTests())
        {
            return;
        }

        var (environment, credentials) = LoadGrvtCredentialsFromDb(DbPath);
        var logger = new AppLogger();
        await using var venue = new GrvtVenueAdapter(environment, credentials, logger);

        var snapshot = await venue.GetAccountSnapshotAsync();

        Assert.NotNull(snapshot);
        Assert.True(snapshot.Balances.Count > 0, "GRVT balances are empty.");
    }

    private static bool ShouldRunLiveTests()
    {
        var flag = Environment.GetEnvironmentVariable("AIYOPERPS_RUN_GRVT_LIVE_TESTS");
        return string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static (string Environment, AccountCredentials Credentials) LoadGrvtCredentialsFromDb(string dbPath)
    {
        var dbFile = new FileInfo(dbPath);
        if (!dbFile.Exists)
        {
            throw new FileNotFoundException($"AiyoPerps DB not found: {dbPath}");
        }

        var dbDir = dbFile.Directory ?? throw new InvalidOperationException("DB directory missing.");
        var keyPath = Path.Combine(dbDir.FullName, "secrets.key");
        if (!File.Exists(keyPath))
        {
            throw new FileNotFoundException($"secrets.key not found: {keyPath}");
        }

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT Environment, ApiKeyEncrypted, ApiSecretEncrypted, AccountAddress, SubAccountId, WalletAddress, PrivateKeyEncrypted, AuthMode
FROM Accounts
WHERE VenueId='GRVT' AND IsEnabled=1
ORDER BY CASE WHEN Environment='testnet' THEN 0 ELSE 1 END, CreatedAt DESC
LIMIT 1";

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("No enabled GRVT account found in DB.");
        }

        var environment = reader.GetString(0);
        var apiKeyEnc = reader.IsDBNull(1) ? null : reader.GetString(1);
        var apiSecretEnc = reader.IsDBNull(2) ? null : reader.GetString(2);
        var accountAddress = reader.IsDBNull(3) ? null : reader.GetString(3);
        var subAccountId = reader.IsDBNull(4) ? null : reader.GetString(4);
        var walletAddress = reader.IsDBNull(5) ? null : reader.GetString(5);
        var privateKeyEnc = reader.IsDBNull(6) ? null : reader.GetString(6);
        var authMode = reader.IsDBNull(7) ? null : reader.GetString(7);

        var key = File.ReadAllBytes(keyPath);
        var credentials = new AccountCredentials
        {
            AuthMode = string.IsNullOrWhiteSpace(authMode) ? "ApiKey" : authMode,
            ApiKey = DecryptOrNull(apiKeyEnc, key),
            ApiSecret = DecryptOrNull(apiSecretEnc, key),
            AccountAddress = accountAddress,
            SubAccountId = subAccountId,
            WalletAddress = walletAddress,
            PrivateKey = DecryptOrNull(privateKeyEnc, key)
        };

        if (string.IsNullOrWhiteSpace(credentials.ApiKey) || string.IsNullOrWhiteSpace(credentials.ApiSecret))
        {
            throw new InvalidOperationException("GRVT API credentials are missing in DB.");
        }

        if (string.IsNullOrWhiteSpace(credentials.SubAccountId))
        {
            throw new InvalidOperationException("GRVT SubAccountId is missing in DB.");
        }

        return (environment, credentials);
    }

    private static string? DecryptOrNull(string? cipher, byte[] key)
    {
        if (string.IsNullOrWhiteSpace(cipher))
        {
            return null;
        }

        var payload = Convert.FromBase64String(cipher);
        using var aes = Aes.Create();
        aes.Key = key;

        var ivLen = aes.BlockSize / 8;
        var iv = payload[..ivLen];
        var data = payload[ivLen..];

        using var decryptor = aes.CreateDecryptor(aes.Key, iv);
        var plain = decryptor.TransformFinalBlock(data, 0, data.Length);
        return Encoding.UTF8.GetString(plain);
    }
}
