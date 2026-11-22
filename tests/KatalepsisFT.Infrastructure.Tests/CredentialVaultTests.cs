using System.Security.Cryptography;
using FluentAssertions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Infrastructure.Security;

namespace KatalepsisFT.Infrastructure.Tests;

public class CredentialVaultTests
{
    private const string MasterKey = "a-master-key-that-is-long-enough-0123456789";
    private static readonly VenueCredentials Credentials = new("key-ABCD", "secret/with+symbols==", "pass phrase");

    [Fact]
    public void Protect_ThenUnprotect_ReturnsTheSameCredentials()
    {
        CredentialVault vault = new(MasterKey);

        string stored = vault.Protect(Credentials);

        vault.Unprotect(stored).Should().Be(Credentials);
        stored.Should().StartWith("v1.").And.NotContain("key-ABCD").And.NotContain("secret");
    }

    [Fact]
    public void Protect_Twice_GivesDifferentText()
    {
        CredentialVault vault = new(MasterKey);

        vault.Protect(Credentials).Should().NotBe(vault.Protect(Credentials));
    }

    [Fact]
    public void Unprotect_WithAnotherMasterKey_OrAChangedValue_Fails()
    {
        string stored = new CredentialVault(MasterKey).Protect(Credentials);
        char[] changed = stored.ToCharArray();
        changed[^6] = changed[^6] == 'A' ? 'B' : 'A';

        FluentActions.Invoking(() => new CredentialVault(MasterKey + "x").Unprotect(stored)).Should().Throw<CryptographicException>();
        FluentActions.Invoking(() => new CredentialVault(MasterKey).Unprotect(new string(changed))).Should().Throw<CryptographicException>();
        FluentActions.Invoking(() => new CredentialVault(MasterKey).Unprotect("plain text")).Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    public void WithoutAProperMasterKey_TheVaultIsUnavailable(string? masterKey)
    {
        CredentialVault vault = new(masterKey);

        vault.IsAvailable.Should().BeFalse();
        FluentActions.Invoking(() => vault.Protect(Credentials)).Should().Throw<InvalidOperationException>();
    }
}
