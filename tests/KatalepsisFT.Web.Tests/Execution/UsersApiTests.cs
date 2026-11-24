using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Structure;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Web.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KatalepsisFT.Web.Tests.Execution;

/// <summary>Adding the people that two-person approval needs, and the kind of fund that belongs to one owner.</summary>
public class UsersApiTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private async Task<(WebApplicationFactory<Program> App, HttpClient Client, string Email)> SignInAsync(string role)
    {
        // The shared app: every test here makes its own users and funds.
        WebApplicationFactory<Program> app = database.App;
        string email = await database.AddUserAsync(app, role);
        HttpClient client = TestDatabase.CreateClient(app);
        await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, TestDatabase.Password, false));
        return (app, client, email);
    }

    [Fact]
    public async Task Admin_AddsATrader_WhoSignsInWithTheTemporaryPassword_AndMustChangeIt()
    {
        (WebApplicationFactory<Program> app, HttpClient admin, string adminEmail) = await SignInAsync(Roles.Admin);
        string email = $"trader-{Guid.NewGuid():N}@example.com";

        HttpResponseMessage added = await admin.PostAsJsonAsync("/api/users", new SaveUserRequest(email, "New Trader", Roles.Trader));
        UserCreatedDto created = (await added.Content.ReadFromJsonAsync<UserCreatedDto>())!;
        HttpResponseMessage again = await admin.PostAsJsonAsync("/api/users", new SaveUserRequest(email, "Twice", Roles.Viewer));
        HttpResponseMessage badRole = await admin.PostAsJsonAsync("/api/users", new SaveUserRequest($"x-{Guid.NewGuid():N}@example.com", "X", "Owner"));
        HttpClient trader = TestDatabase.CreateClient(app);
        HttpResponseMessage signedIn = await trader.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, created.TemporaryPassword, false));
        List<UserDto> all = (await admin.GetFromJsonAsync<List<UserDto>>("/api/users"))!;
        UserDto me = all.Single(u => u.Email == adminEmail);

        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        created.User.Role.Should().Be(Roles.Trader);
        created.User.MustChangePassword.Should().BeTrue();
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        badRole.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        signedIn.StatusCode.Should().Be(HttpStatusCode.OK);
        // Nobody changes their own role or switches themselves off, and a trader cannot manage users at all.
        (await admin.PostAsJsonAsync($"/api/users/{me.Id}/role", new SetRoleRequest(Roles.Viewer))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync($"/api/users/{me.Id}/disabled", new SetDisabledRequest(true))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync($"/api/users/{created.User.Id}/disabled", new SetDisabledRequest(true))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await TestDatabase.CreateClient(app).PostAsJsonAsync("/api/auth/login", new LoginRequest(email, created.TemporaryPassword, false))).StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await (await SignInAsync(Roles.Trader)).Client.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MakingAnAdministrator_TakesASecondAdministrator_AndNobodyResetsAnAdministratorsPassword()
    {
        (WebApplicationFactory<Program> app, HttpClient first, string firstEmail) = await SignInAsync(Roles.Admin);
        (_, HttpClient second, _) = await SignInAsync(Roles.Admin);
        string email = $"admin-{Guid.NewGuid():N}@example.com";

        // Put forward by the first administrator: the account exists, but as a Viewer.
        UserCreatedDto created = (await (await first.PostAsJsonAsync("/api/users", new SaveUserRequest(email, "Second Self", Roles.Admin))).Content.ReadFromJsonAsync<UserCreatedDto>())!;
        HttpClient newcomer = TestDatabase.CreateClient(app);
        await newcomer.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, created.TemporaryPassword, false));
        HttpResponseMessage byProposer = await first.PostAsJsonAsync($"/api/users/{created.User.Id}/confirm-admin", new { });
        HttpResponseMessage byAnother = await second.PostAsJsonAsync($"/api/users/{created.User.Id}/confirm-admin", new { });
        UserDto confirmed = (await byAnother.Content.ReadFromJsonAsync<UserDto>())!;
        HttpResponseMessage twice = await second.PostAsJsonAsync($"/api/users/{created.User.Id}/confirm-admin", new { });
        HttpResponseMessage reset = await first.PostAsJsonAsync($"/api/users/{created.User.Id}/reset-password", new { });

        created.User.Role.Should().Be(Roles.Viewer);
        created.User.AdminProposedBy.Should().Be(firstEmail);
        byProposer.StatusCode.Should().Be(HttpStatusCode.Conflict, "whoever asks cannot also agree");
        byAnother.StatusCode.Should().Be(HttpStatusCode.OK, await byAnother.Content.ReadAsStringAsync());
        confirmed.Role.Should().Be(Roles.Admin);
        confirmed.AdminProposedBy.Should().BeNull();
        twice.StatusCode.Should().Be(HttpStatusCode.Conflict);
        reset.StatusCode.Should().Be(HttpStatusCode.Conflict, "a reset hands the new password to whoever asked for it");
    }

    [Fact]
    public async Task PromotingSomeoneToAdministrator_WaitsForASecondAdministrator()
    {
        (_, HttpClient first, string firstEmail) = await SignInAsync(Roles.Admin);
        (_, HttpClient second, _) = await SignInAsync(Roles.Admin);
        UserCreatedDto trader = (await (await first.PostAsJsonAsync("/api/users", new SaveUserRequest($"t-{Guid.NewGuid():N}@example.com", "Trader", Roles.Trader))).Content.ReadFromJsonAsync<UserCreatedDto>())!;

        UserDto proposed = (await (await first.PostAsJsonAsync($"/api/users/{trader.User.Id}/role", new SetRoleRequest(Roles.Admin))).Content.ReadFromJsonAsync<UserDto>())!;
        UserDto agreed = (await (await second.PostAsJsonAsync($"/api/users/{trader.User.Id}/confirm-admin", new { })).Content.ReadFromJsonAsync<UserDto>())!;

        proposed.Role.Should().Be(Roles.Trader, "the person keeps their role until a second administrator agrees");
        proposed.AdminProposedBy.Should().Be(firstEmail);
        agreed.Role.Should().Be(Roles.Admin);
    }

    [Fact]
    public async Task SeparatelyManagedAccount_NeedsItsOwner_AndIsListedAsOne()
    {
        (_, HttpClient admin, _) = await SignInAsync(Roles.Admin);
        string name = $"SMA {Guid.NewGuid():N}";
        NewFundRequest request = new(name, null, [new NewSleeveRequest("Core", MandateType.Directional, [new NewManagerRequest("Desk", 0m)])], FundKind.Sma, " ");

        HttpResponseMessage noOwner = await admin.PostAsJsonAsync("/api/funds", request);
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/funds", request with { OwnerName = "A Client" });
        string listed = await admin.GetStringAsync("/api/funds");

        noOwner.StatusCode.Should().NotBe(HttpStatusCode.OK);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        listed.Should().Contain(name).And.Contain("\"kind\":\"Sma\"").And.Contain("\"ownerName\":\"A Client\"");
    }
}
