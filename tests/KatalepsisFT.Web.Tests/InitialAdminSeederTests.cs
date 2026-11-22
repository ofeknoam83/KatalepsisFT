using FluentAssertions;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Persistence.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KatalepsisFT.Web.Tests;

public class InitialAdminSeederTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    // One test, in order, because the outcome depends on whether the database already holds a user.
    [Fact]
    public async Task EnsureInitialAdmin_CreatesOnceAndOnlyWhenConfigured()
    {
        WebApplicationFactory<Program> app = database.CreateApp();
        using IServiceScope scope = app.Services.CreateScope();
        InitialAdminSeeder seeder = ActivatorUtilities.CreateInstance<InitialAdminSeeder>(scope.ServiceProvider);
        UserManager<AppUser> users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        InitialAdminOutcome withoutSettings = await seeder.EnsureInitialAdminAsync(null, null, CancellationToken.None);
        // A generated secret may not satisfy the password rules; the first account is created anyway.
        InitialAdminOutcome first = await seeder.EnsureInitialAdminAsync("first.admin@example.com", "weakgenerated", CancellationToken.None);
        InitialAdminOutcome second = await seeder.EnsureInitialAdminAsync("someone.else@example.com", "weakgenerated", CancellationToken.None);

        withoutSettings.Should().Be(InitialAdminOutcome.NotConfigured);
        first.Should().Be(InitialAdminOutcome.Created);
        second.Should().Be(InitialAdminOutcome.UsersAlreadyExist);
        AppUser admin = (await users.FindByEmailAsync("first.admin@example.com"))!;
        admin.MustChangePassword.Should().BeTrue();
        (await users.IsInRoleAsync(admin, Roles.Admin)).Should().BeTrue();
        (await users.CheckPasswordAsync(admin, "weakgenerated")).Should().BeTrue();
        (await users.FindByEmailAsync("someone.else@example.com")).Should().BeNull();
    }
}
