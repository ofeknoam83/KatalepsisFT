using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KatalepsisFT.Application;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Domain.Instruments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace KatalepsisFT.Infrastructure.Execution;

// ---------------------------------------------------------------- the vault for trading keys

/// <summary>
/// Trading keys are sealed with a public key and can only be opened with the matching private key. The private key
/// is kept encrypted under EXEC_MASTER_KEY, a secret only the execution service is given. So the web service, which
/// takes a key in, can lock it away but can never read it back; and a copy of the database is not enough to open one.
///
/// A sealed key is "s1." and base64 of: the length of the wrapped key, the wrapped one-time key, nonce, tag, ciphertext.
/// </summary>
public sealed class SealedTradingVault(IServiceScopeFactory scopes) : ITradingVault
{
    public const string MasterKeySetting = "EXEC_MASTER_KEY";
    public const string PublicKeyName = "exec.vault.public";
    public const string PrivateKeyName = "exec.vault.private";
    private const string Prefix = "s1.";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private byte[]? _publicKey;

    public async Task<bool> CanSealAsync(CancellationToken cancellationToken) => await PublicKeyAsync(cancellationToken) is not null;

    public async Task<string> SealAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        byte[] publicKey = await PublicKeyAsync(cancellationToken) ?? throw new InvalidOperationException("The execution service has not published its key yet.");
        using RSA rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        byte[] oneTime = RandomNumberGenerator.GetBytes(32);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagSize];
        using (AesGcm aes = new(oneTime, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        byte[] wrapped = rsa.Encrypt(oneTime, RSAEncryptionPadding.OaepSHA256);
        CryptographicOperations.ZeroMemory(plain);
        CryptographicOperations.ZeroMemory(oneTime);
        return Prefix + Convert.ToBase64String([.. BitConverter.GetBytes((ushort)wrapped.Length), .. wrapped, .. nonce, .. tag, .. cipher]);
    }

    private async Task<byte[]?> PublicKeyAsync(CancellationToken cancellationToken)
    {
        if (_publicKey is not null)
        {
            return _publicKey;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        string? stored = await scope.ServiceProvider.GetRequiredService<IFtDbContext>().SystemSettings.Where(s => s.Name == PublicKeyName).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);
        return stored is null ? null : _publicKey = Convert.FromBase64String(stored);
    }

    /// <summary>
    /// Run by the execution service when it starts: makes the key pair the first time, and afterwards proves it can
    /// still open what was sealed. Returns the opener, which never leaves the execution service.
    /// </summary>
    public static async Task<TradingKeyOpener?> EnsureKeysAsync(IFtDbContext database, string? masterKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(masterKey) || masterKey.Trim().Length < 32)
        {
            return null;
        }

        byte[] master = SHA256.HashData(Encoding.UTF8.GetBytes(masterKey.Trim()));
        string? storedPrivate = await database.SystemSettings.Where(s => s.Name == PrivateKeyName).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);
        if (storedPrivate is null)
        {
            using RSA fresh = RSA.Create(3072);
            byte[] privateKey = fresh.ExportPkcs8PrivateKey();
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] cipher = new byte[privateKey.Length];
            byte[] tag = new byte[TagSize];
            using (AesGcm aes = new(master, TagSize))
            {
                aes.Encrypt(nonce, privateKey, cipher, tag);
            }

            database.SystemSettings.Add(new Domain.Execution.SystemSetting { Name = PublicKeyName, Value = Convert.ToBase64String(fresh.ExportSubjectPublicKeyInfo()) });
            database.SystemSettings.Add(new Domain.Execution.SystemSetting { Name = PrivateKeyName, Value = Convert.ToBase64String([.. nonce, .. tag, .. cipher]) });
            await database.SaveChangesAsync(cancellationToken);
            return new TradingKeyOpener(privateKey);
        }

        byte[] data = Convert.FromBase64String(storedPrivate);
        byte[] opened = new byte[data.Length - NonceSize - TagSize];
        using (AesGcm aes = new(master, TagSize))
        {
            // Throws when EXEC_MASTER_KEY is not the one the pair was made under: better to stop than to run unable to open keys.
            aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), opened);
        }

        return new TradingKeyOpener(opened);
    }

    /// <summary>Opens sealed trading keys. Exists only inside the execution service.</summary>
    public sealed class TradingKeyOpener(byte[] privateKey)
    {
        public VenueCredentials Open(string sealedKey)
        {
            if (!sealedKey.StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new CryptographicException("The stored trading key is not in a known format.");
            }

            byte[] data = Convert.FromBase64String(sealedKey[Prefix.Length..]);
            int wrappedLength = BitConverter.ToUInt16(data, 0);
            using RSA rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(privateKey, out _);
            byte[] oneTime = rsa.Decrypt(data.AsSpan(2, wrappedLength).ToArray(), RSAEncryptionPadding.OaepSHA256);
            int at = 2 + wrappedLength;
            byte[] plain = new byte[data.Length - at - NonceSize - TagSize];
            using AesGcm aes = new(oneTime, TagSize);
            aes.Decrypt(data.AsSpan(at, NonceSize), data.AsSpan(at + NonceSize + TagSize), data.AsSpan(at + NonceSize, TagSize), plain);
            return JsonSerializer.Deserialize<VenueCredentials>(plain)!;
        }
    }
}

// ---------------------------------------------------------------- the book and the wake-up call, over Key Value

/// <summary>
/// The consolidated book as other services see it. The execution service writes each instrument's venues a few times
/// a second with a short life; a reader that finds nothing knows the feed is down rather than reading old prices.
/// </summary>
public sealed class KeyValueBookFeed(IConnectionMultiplexer keyValue) : IBookFeed
{
    public static readonly TimeSpan Life = TimeSpan.FromSeconds(3);
    private const string IndexKey = "ft:book:index";
    private const string StatusKey = "ft:exec:status";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static string Key(string asset, InstrumentType type) => $"ft:book:{type}:{asset.ToUpperInvariant()}";

    /// <summary>Called by the execution service: everything it currently knows, in one round trip.</summary>
    public async Task PublishAsync(ConsolidatedBook books, ExecutionStatus? status = null)
    {
        IReadOnlyList<LiveInstrument> instruments = books.Instruments();
        IBatch batch = keyValue.GetDatabase().CreateBatch();
        List<Task> writes = [batch.StringSetAsync(IndexKey, JsonSerializer.Serialize(instruments, Json), Life)];
        foreach ((string asset, InstrumentType type, _) in instruments)
        {
            writes.Add(batch.StringSetAsync(Key(asset, type), JsonSerializer.Serialize(books.For(asset, type), Json), Life));
        }

        if (status is not null)
        {
            writes.Add(batch.StringSetAsync(StatusKey, JsonSerializer.Serialize(status, Json), Life));
        }

        batch.Execute();
        await Task.WhenAll(writes);
    }

    public async Task<ExecutionStatus?> StatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            RedisValue stored = await keyValue.GetDatabase().StringGetAsync(StatusKey);
            return stored.IsNullOrEmpty ? null : JsonSerializer.Deserialize<ExecutionStatus>((string)stored!, Json);
        }
        catch (RedisException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<VenueBook>> ReadAsync(string asset, InstrumentType type, CancellationToken cancellationToken)
    {
        try
        {
            RedisValue stored = await keyValue.GetDatabase().StringGetAsync(Key(asset, type));
            return stored.IsNullOrEmpty ? [] : JsonSerializer.Deserialize<List<VenueBook>>((string)stored!, Json) ?? [];
        }
        catch (RedisException)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<LiveInstrument>> InstrumentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            RedisValue stored = await keyValue.GetDatabase().StringGetAsync(IndexKey);
            return stored.IsNullOrEmpty ? [] : JsonSerializer.Deserialize<List<LiveInstrument>>((string)stored!, Json) ?? [];
        }
        catch (RedisException)
        {
            return [];
        }
    }
}

/// <summary>For a service with no Key Value store: there is no book to show.</summary>
public sealed class NoBookFeed : IBookFeed
{
    public Task<IReadOnlyList<VenueBook>> ReadAsync(string asset, InstrumentType type, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VenueBook>>([]);

    public Task<IReadOnlyList<LiveInstrument>> InstrumentsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LiveInstrument>>([]);

    public Task<ExecutionStatus?> StatusAsync(CancellationToken cancellationToken) => Task.FromResult<ExecutionStatus?>(null);
}

/// <summary>The book of the process itself, for the execution service and for tests.</summary>
public sealed class LocalBookFeed(ConsolidatedBook books) : IBookFeed
{
    public Task<IReadOnlyList<VenueBook>> ReadAsync(string asset, InstrumentType type, CancellationToken cancellationToken) => Task.FromResult(books.For(asset, type));

    public Task<IReadOnlyList<LiveInstrument>> InstrumentsAsync(CancellationToken cancellationToken) => Task.FromResult(books.Instruments());

    public Task<ExecutionStatus?> StatusAsync(CancellationToken cancellationToken) => Task.FromResult<ExecutionStatus?>(null);
}

/// <summary>"There is a new order", sent over Key Value so the execution service acts on it within milliseconds instead of on its next round.</summary>
public sealed class KeyValueExecutionSignal(IConnectionMultiplexer keyValue, ILogger<KeyValueExecutionSignal> logger) : IExecutionSignal
{
    public const string Channel = "ft:exec:wake";

    public async Task WakeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await keyValue.GetSubscriber().PublishAsync(RedisChannel.Literal(Channel), 1);
        }
        catch (RedisException e)
        {
            // The order is already written down; the execution service finds it on its next round.
            logger.LogWarning(e, "The execution service could not be woken.");
        }
    }
}

public sealed class NoExecutionSignal : IExecutionSignal
{
    public Task WakeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
