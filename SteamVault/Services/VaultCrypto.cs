using System.Security.Cryptography;
using System.Text;

namespace SteamVault.Services;

/// <summary>
/// AES-256-GCM vault encryption (concept from SAM vault crypto).
/// </summary>
public static class VaultCrypto
{
    private const int Iterations = 200_000;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    public static byte[] Encrypt(string plainText, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Derive(password, salt);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plainText);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var result = new byte[SaltSize + NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(salt, 0, result, 0, SaltSize);
        Buffer.BlockCopy(nonce, 0, result, SaltSize, NonceSize);
        Buffer.BlockCopy(tag, 0, result, SaltSize + NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, result, SaltSize + NonceSize + TagSize, cipher.Length);
        return result;
    }

    public static string Decrypt(byte[] blob, string password)
    {
        if (blob.Length < SaltSize + NonceSize + TagSize)
            throw new CryptographicException("Vault corrupt");

        var salt = blob.AsSpan(0, SaltSize).ToArray();
        var nonce = blob.AsSpan(SaltSize, NonceSize).ToArray();
        var tag = blob.AsSpan(SaltSize + NonceSize, TagSize).ToArray();
        var cipher = blob.AsSpan(SaltSize + NonceSize + TagSize).ToArray();
        var key = Derive(password, salt);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Derive(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySize);
    }
}
