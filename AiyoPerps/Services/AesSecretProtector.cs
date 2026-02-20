using AiyoPerps.Data;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AiyoPerps.Services;

public sealed class AesSecretProtector : ISecretProtector
{
    private const int KeySizeBytes = 32;
    private static readonly string KeyPath = Path.Combine(AppDbContext.DbDirectory, "secrets.key");

    public string Protect(string plainText)
    {
        using var aes = Aes.Create();
        aes.Key = LoadOrCreateKey();
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var payload = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, payload, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, payload, aes.IV.Length, cipherBytes.Length);
        return Convert.ToBase64String(payload);
    }

    public string Unprotect(string cipherText)
    {
        var payload = Convert.FromBase64String(cipherText);
        using var aes = Aes.Create();
        aes.Key = LoadOrCreateKey();

        var iv = new byte[aes.BlockSize / 8];
        var cipherBytes = new byte[payload.Length - iv.Length];

        Buffer.BlockCopy(payload, 0, iv, 0, iv.Length);
        Buffer.BlockCopy(payload, iv.Length, cipherBytes, 0, cipherBytes.Length);

        using var decryptor = aes.CreateDecryptor(aes.Key, iv);
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] LoadOrCreateKey()
    {
        Directory.CreateDirectory(AppDbContext.DbDirectory);
        if (File.Exists(KeyPath))
        {
            return File.ReadAllBytes(KeyPath);
        }

        var key = RandomNumberGenerator.GetBytes(KeySizeBytes);
        File.WriteAllBytes(KeyPath, key);
        return key;
    }
}
