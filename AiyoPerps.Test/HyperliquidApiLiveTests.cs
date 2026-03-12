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

public sealed class HyperliquidApiLiveTests
{
    private static readonly string DbPath = Environment.GetEnvironmentVariable("AIYOPERPS_DB_PATH")
        ?? @"E:\work\AiyoPerps\AiyoPerps\AiyoPerps\bin\Debug\net10.0\db\AiyoPerps.main.db";

    [Fact]
    public async Task ProbeAccountSnapshot_FromStoredCredentials_ShouldCollectSamples()
    {
        if (!ShouldRunLiveTests())
        {
            return;
        }

        var (environment, credentials) = LoadHyperliquidCredentialsFromDb(DbPath);
        var logger = new AppLogger();
        await using var venue = new HyperliquidVenueAdapter(environment, credentials, logger);

        var durationSeconds = ParseProbeSeconds();
        var endAt = DateTimeOffset.UtcNow.AddSeconds(durationSeconds);
        var iteration = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds + 30));

        while (DateTimeOffset.UtcNow < endAt && !cts.IsCancellationRequested)
        {
            var sections = iteration % 3 == 0
                ? AccountSnapshotSections.All
                : AccountSnapshotSections.Positions | AccountSnapshotSections.Orders;

            var snapshot = await venue.GetAccountSnapshotAsync(sections, cts.Token);
            Assert.NotNull(snapshot);

            iteration++;
            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
        }

        Assert.True(iteration > 0, "Hyperliquid live probe did not execute any iterations.");
    }

    private static bool ShouldRunLiveTests()
    {
        var flag = Environment.GetEnvironmentVariable("AIYOPERPS_RUN_HYPERLIQUID_LIVE_TESTS");
        return string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseProbeSeconds()
    {
        var raw = Environment.GetEnvironmentVariable("AIYOPERPS_HYPERLIQUID_LIVE_PROBE_SECONDS");
        return int.TryParse(raw, out var seconds) && seconds > 0 ? seconds : 90;
    }

    private static (string Environment, AccountCredentials Credentials) LoadHyperliquidCredentialsFromDb(string dbPath)
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
SELECT Environment, AccountAddress, WalletAddress, PrivateKeyEncrypted, ApiKeyEncrypted, ApiSecretEncrypted, AuthMode
FROM Accounts
WHERE VenueId='Hyperliquid' AND IsEnabled=1
ORDER BY CASE WHEN Environment='mainnet' THEN 0 ELSE 1 END, CreatedAt DESC
LIMIT 1";

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("No enabled Hyperliquid account found in DB.");
        }

        var environment = reader.GetString(0);
        var accountAddress = reader.IsDBNull(1) ? null : reader.GetString(1);
        var walletAddress = reader.IsDBNull(2) ? null : reader.GetString(2);
        var privateKeyEnc = reader.IsDBNull(3) ? null : reader.GetString(3);
        var apiKeyEnc = reader.IsDBNull(4) ? null : reader.GetString(4);
        var apiSecretEnc = reader.IsDBNull(5) ? null : reader.GetString(5);
        var authMode = reader.IsDBNull(6) ? null : reader.GetString(6);

        var key = File.ReadAllBytes(keyPath);
        var credentials = new AccountCredentials
        {
            AuthMode = string.IsNullOrWhiteSpace(authMode) ? "Wallet" : authMode,
            AccountAddress = accountAddress,
            WalletAddress = walletAddress,
            PrivateKey = DecryptOrNull(privateKeyEnc, key),
            ApiKey = DecryptOrNull(apiKeyEnc, key),
            ApiSecret = DecryptOrNull(apiSecretEnc, key)
        };

        if (string.IsNullOrWhiteSpace(credentials.WalletAddress) && string.IsNullOrWhiteSpace(credentials.AccountAddress))
        {
            throw new InvalidOperationException("Hyperliquid credentials are missing account address and wallet address.");
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
