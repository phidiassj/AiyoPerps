using AiyoPerps.Models;
using AiyoPerps.Services;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace AiyoPerps.Test;

public sealed class AsterApiLiveTests
{
    private static readonly string DbPath = Environment.GetEnvironmentVariable("AIYOPERPS_DB_PATH")
        ?? "/mnt/e/work/AiyoPerps/AiyoPerps/AiyoPerps/bin/Debug/net10.0/db/AiyoPerps.main.db";

    [Fact]
    public async Task ValidateConnection_MainnetAster_FromStoredWallet_ShouldSucceed()
    {
        if (!ShouldRunLiveTests())
        {
            return;
        }

        var (environment, credentials) = LoadAsterCredentialsFromDb(DbPath);
        var logger = new AppLogger();
        await using var venue = new AsterVenueAdapter(environment, credentials, logger);

        var result = await venue.ValidateConnectionAsync();

        Assert.True(result.IsSuccess, $"Aster ValidateConnection failed: {result.Message}");
    }

    [Fact]
    public async Task GetAccountSnapshot_MainnetAster_FromStoredWallet_ShouldReturnBalances()
    {
        if (!ShouldRunLiveTests())
        {
            return;
        }

        var (environment, credentials) = LoadAsterCredentialsFromDb(DbPath);
        var logger = new AppLogger();
        await using var venue = new AsterVenueAdapter(environment, credentials, logger);

        var snapshot = await venue.GetAccountSnapshotAsync();

        Assert.NotNull(snapshot);
        Assert.True(snapshot.Balances.Count > 0, "Aster balances are empty.");
    }

    private static bool ShouldRunLiveTests()
    {
        var flag = Environment.GetEnvironmentVariable("AIYOPERPS_RUN_ASTER_LIVE_TESTS");
        return string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static (string Environment, AccountCredentials Credentials) LoadAsterCredentialsFromDb(string dbPath)
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
SELECT Environment, AccountAddress, WalletAddress, ApiKeyEncrypted, ApiSecretEncrypted, PrivateKeyEncrypted
FROM Accounts
WHERE VenueId='Aster' AND IsEnabled=1
ORDER BY CASE WHEN Environment='mainnet' THEN 0 ELSE 1 END, CreatedAt DESC
LIMIT 1";

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("No enabled Aster account found in DB.");
        }

        var environment = reader.GetString(0);
        var accountAddress = reader.IsDBNull(1) ? null : reader.GetString(1);
        var walletAddress = reader.IsDBNull(2) ? null : reader.GetString(2);
        var apiKeyEnc = reader.IsDBNull(3) ? null : reader.GetString(3);
        var apiSecretEnc = reader.IsDBNull(4) ? null : reader.GetString(4);
        var privateKeyEnc = reader.IsDBNull(5) ? null : reader.GetString(5);

        var key = File.ReadAllBytes(keyPath);

        var credentials = new AccountCredentials
        {
            AccountAddress = accountAddress,
            WalletAddress = walletAddress,
            ApiKey = DecryptOrNull(apiKeyEnc, key),
            ApiSecret = DecryptOrNull(apiSecretEnc, key),
            PrivateKey = DecryptOrNull(privateKeyEnc, key)
        };

        if (string.IsNullOrWhiteSpace(credentials.AccountAddress) ||
            string.IsNullOrWhiteSpace(credentials.WalletAddress) ||
            string.IsNullOrWhiteSpace(credentials.PrivateKey))
        {
            throw new InvalidOperationException("Aster credentials are incomplete in DB.");
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
