using FluentAssertions;
using KatalepsisFT.Persistence.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace KatalepsisFT.Web.Tests;

public class PasswordRecoveryTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    [Fact]
    public async Task PasswordRecovery_GivesATemporaryPasswordAndLiftsAWrongPasswordLock()
    {
        using IServiceScope scope = database.App.Services.CreateScope();
        UserManager<AppUser> users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        AppUser user = new() { UserName = "locked.out@example.com", Email = "locked.out@example.com", DisplayName = "Locked Out", CreatedAt = DateTimeOffset.UtcNow, LockoutEnabled = true, LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(10), AccessFailedCount = 5 };
        (await users.CreateAsync(user, "The-old-password-1A")).Succeeded.Should().BeTrue();
        PasswordRecovery recovery = ActivatorUtilities.CreateInstance<PasswordRecovery>(scope.ServiceProvider);

        string? password = await recovery.ResetAsync(" Locked.Out@example.com ", CancellationToken.None);
        string? nobody = await recovery.ResetAsync("nobody@example.com", CancellationToken.None);

        nobody.Should().BeNull();
        password.Should().NotBeNull();
        AppUser after = (await users.FindByEmailAsync("locked.out@example.com"))!;
        (await users.CheckPasswordAsync(after, password!)).Should().BeTrue();
        (await users.CheckPasswordAsync(after, "The-old-password-1A")).Should().BeFalse();
        after.MustChangePassword.Should().BeTrue();
        after.LockoutEnd.Should().BeNull();
        after.AccessFailedCount.Should().Be(0);
    }

    [Fact]
    public async Task PasswordRecovery_LeavesASwitchedOffAccountSwitchedOff()
    {
        using IServiceScope scope = database.App.Services.CreateScope();
        UserManager<AppUser> users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        AppUser user = new() { UserName = "switched.off@example.com", Email = "switched.off@example.com", DisplayName = "Switched Off", CreatedAt = DateTimeOffset.UtcNow, LockoutEnabled = true, LockoutEnd = DateTimeOffset.MaxValue };
        (await users.CreateAsync(user, "The-old-password-1A")).Succeeded.Should().BeTrue();

        await ActivatorUtilities.CreateInstance<PasswordRecovery>(scope.ServiceProvider).ResetAsync("switched.off@example.com", CancellationToken.None);

        (await users.FindByEmailAsync("switched.off@example.com"))!.LockoutEnd.Should().NotBeNull();
    }
}
