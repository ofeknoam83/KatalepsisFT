using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KatalepsisFT.Application.Connections;

namespace KatalepsisFT.Infrastructure.Security;

/// <summary>
/// Encrypts venue keys with AES-256-GCM under a key derived from the MASTER_KEY environment secret. The stored text is
/// "v1." followed by base64 of nonce, tag and ciphertext; a changed or truncated value fails to decrypt instead of
/// yielding garbage.
/// </summary>
public sealed class CredentialVault : ICredentialVault
{
    public const string MasterKeySetting = "MASTER_KEY";

    /// <summary>Shorter secrets are refused: they would make the stored keys guessable.</summary>
    public const int MinimumMasterKeyLength = 32;

    private const string Prefix = "v1.";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[]? _key;

    public CredentialVault(string? masterKey)
    {
        _key = string.IsNullOrWhiteSpace(masterKey) || masterKey.Trim().Length < MinimumMasterKeyLength ? null : SHA256.HashData(Encoding.UTF8.GetBytes(masterKey.Trim()));
    }

    public bool IsAvailable => _key is not null;

    public string Protect(VenueCredentials credentials)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagSize];
        using AesGcm aes = new(Key(), TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        CryptographicOperations.ZeroMemory(plain);
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public VenueCredentials Unprotect(string protectedCredentials)
    {
        if (!protectedCredentials.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("The stored key is not in a known format.");
        }

        byte[] data = Convert.FromBase64String(protectedCredentials[Prefix.Length..]);
        if (data.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The stored key is incomplete.");
        }

        byte[] plain = new byte[data.Length - NonceSize - TagSize];
        using AesGcm aes = new(Key(), TagSize);
        aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain);
        VenueCredentials credentials = JsonSerializer.Deserialize<VenueCredentials>(plain) ?? throw new CryptographicException("The stored key is empty.");
        CryptographicOperations.ZeroMemory(plain);
        return credentials;
    }

    private byte[] Key() => _key ?? throw new InvalidOperationException($"{MasterKeySetting} is not configured.");
}
