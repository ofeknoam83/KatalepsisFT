using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace KatalepsisFT.Web.Auth;

/// <summary>
/// The keys that sign session cookies are kept in the database so that every web instance accepts a session. Kept
/// there as they are, a copy of the database would be enough to forge anybody's session. So they are stored
/// encrypted (AES-256-GCM) under a key made from the MASTER_KEY secret, which is not in the database.
/// </summary>
public sealed class SessionKeySecret(string masterKey)
{
    public byte[] Key { get; } = SHA256.HashData(Encoding.UTF8.GetBytes("katalepsis-ft session keys\n" + masterKey.Trim()));
}

public sealed class SessionKeyEncryptor(SessionKeySecret secret) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        byte[] plain = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (AesGcm aes = new(secret.Key, tag.Length))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        XElement sealedKey = new("sealedSessionKey", new XAttribute("v", "1"), Convert.ToBase64String([.. nonce, .. tag, .. cipher]));
        return new EncryptedXmlInfo(sealedKey, typeof(SessionKeyDecryptor));
    }
}

/// <summary>Opens what <see cref="SessionKeyEncryptor"/> sealed. Made by the framework, which hands it the services.</summary>
public sealed class SessionKeyDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        // A different MASTER_KEY makes this throw: the key ring is then unreadable and everybody signs in again,
        // which is the right outcome for a secret that has changed.
        byte[] key = services.GetRequiredService<SessionKeySecret>().Key;
        byte[] data = Convert.FromBase64String(encryptedElement.Value);
        byte[] plain = new byte[data.Length - 28];
        using AesGcm aes = new(key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
        return XElement.Parse(Encoding.UTF8.GetString(plain));
    }
}

/// <summary>
/// The key ring as stored in the database, but showing only the keys that were stored sealed. Keys written before
/// sealing was introduced are still in the table in the clear; anyone holding a copy of the database could sign
/// cookies with them, so they are no longer accepted. Sessions issued under them end, once.
/// </summary>
public sealed class SealedOnlyKeyRepository(Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository stored) : Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements() => stored.GetAllElements().Where(key => key.Descendants("sealedSessionKey").Any()).ToList();

    public void StoreElement(XElement element, string friendlyName) => stored.StoreElement(element, friendlyName);
}
