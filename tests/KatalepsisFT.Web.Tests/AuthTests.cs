using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Domain.Access;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KatalepsisFT.Web.Tests;

public class AuthTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private const string NewPassword = "Another-Strong-7-Password";

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password, RememberMe: false));

    [Fact]
    public async Task Login_ValidCredentials_ReturnsSessionAndMeWorks()
    {
        WebApplicationFactory<Program> app = database.App;
        string email = await database.AddUserAsync(app, Roles.Viewer);
        HttpClient client = TestDatabase.CreateClient(app);

        HttpResponseMessage login = await LoginAsync(client, email, TestDatabase.Password);
        SessionResponse? me = await client.GetFromJsonAsync<SessionResponse>("/api/auth/me");

        login.StatusCode.Should().Be(HttpStatusCode.OK);
        me!.Email.Should().Be(email);
        me.Roles.Should().Equal(Roles.Viewer);
        me.MustChangePassword.Should().BeFalse();
    }

    [Fact]
    public async Task Login_WrongPasswordOrUnknownEmail_Returns401WithTheSameMessage()
    {
        WebApplicationFactory<Program> app = database.App;
        string email = await database.AddUserAsync(app, Roles.Viewer);
        HttpClient client = TestDatabase.CreateClient(app);

        HttpResponseMessage wrongPassword = await LoginAsync(client, email, "not-the-password");
        HttpResponseMessage unknownEmail = await LoginAsync(client, "nobody@example.com", TestDatabase.Password);

        wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknownEmail.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await wrongPassword.Content.ReadAsStringAsync()).Should().Be(await unknownEmail.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_AfterFiveFailures_IsLockedEvenWithTheRightPassword()
    {
        WebApplicationFactory<Program> app = database.App;
        string email = await database.AddUserAsync(app, Roles.Viewer);
        HttpClient client = TestDatabase.CreateClient(app);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            await LoginAsync(client, email, "not-the-password");
        }

        HttpResponseMessage response = await LoginAsync(client, email, TestDatabase.Password);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Me_WithoutSession_Returns401NotARedirect()
    {
        HttpClient client = TestDatabase.CreateClient(database.App);

        HttpResponseMessage response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WithoutTheRequestHeader_Returns400()
    {
        WebApplicationFactory<Program> app = database.App;
        string email = await database.AddUserAsync(app, Roles.Viewer);
        HttpClient client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        HttpResponseMessage response = await LoginAsync(client, email, TestDatabase.Password);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Logout_EndsTheSession()
    {
        WebApplicationFactory<Program> app = database.App;
        string email = await database.AddUserAsync(app, Roles.Viewer);
        HttpClient client = TestDatabase.CreateClient(app);
        await LoginAsync(client, email, TestDatabase.Password);

        HttpResponseMessage logout = await client.PostAsync("/api/auth/logout", null);
        HttpResponseMessage me = await client.GetAsync("/api/auth/me");

        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SystemStatus_ViewerIsRefused_AdminIsAllowed()
    {
        WebApplicationFactory<Program> app = database.App;
        string viewer = await database.AddUserAsync(app, Roles.Viewer);
        string admin = await database.AddUserAsync(app, Roles.Admin);
        HttpClient viewerClient = TestDatabase.CreateClient(app);
        HttpClient adminClient = TestDatabase.CreateClient(app);
        await LoginAsync(viewerClient, viewer, TestDatabase.Password);
        await LoginAsync(adminClient, admin, TestDatabase.Password);

        HttpResponseMessage asViewer = await viewerClient.GetAsync("/api/system/status");
        HttpResponseMessage asAdmin = await adminClient.GetAsync("/api/system/status");
        HttpResponseMessage anonymous = await TestDatabase.CreateClient(app).GetAsync("/api/system/status");

        asViewer.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        asAdmin.StatusCode.Should().Be(HttpStatusCode.OK);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MustChangePassword_BlocksEverythingButAuth_UntilThePasswordIsChanged()
    {
        WebApplicationFactory<Program> app = database.App;
        string email = await database.AddUserAsync(app, Roles.Admin, mustChangePassword: true);
        HttpClient client = TestDatabase.CreateClient(app);

        HttpResponseMessage login = await LoginAsync(client, email, TestDatabase.Password);
        SessionResponse? session = await login.Content.ReadFromJsonAsync<SessionResponse>();
        HttpResponseMessage blocked = await client.GetAsync("/api/system/status");
        HttpResponseMessage change = await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(TestDatabase.Password, NewPassword));
        HttpResponseMessage allowed = await client.GetAsync("/api/system/status");

        session!.MustChangePassword.Should().BeTrue();
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        change.StatusCode.Should().Be(HttpStatusCode.OK);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChangePassword_WeakNewPassword_IsRejectedAndTheOldOneStillWorks()
    {
        WebApplicationFactory<Program> app = database.App;
        string email = await database.AddUserAsync(app, Roles.Viewer);
        HttpClient client = TestDatabase.CreateClient(app);
        await LoginAsync(client, email, TestDatabase.Password);

        HttpResponseMessage change = await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(TestDatabase.Password, "short"));
        HttpResponseMessage loginAgain = await LoginAsync(TestDatabase.CreateClient(app), email, TestDatabase.Password);

        change.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        loginAgain.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Session_IssuedByOneInstance_IsAcceptedByAnother()
    {
        WebApplicationFactory<Program> first = database.App;
        WebApplicationFactory<Program> second = database.CreateApp();
        string email = await database.AddUserAsync(first, Roles.Viewer);
        HttpClient onFirst = TestDatabase.CreateClient(first);
        HttpResponseMessage login = await LoginAsync(onFirst, email, TestDatabase.Password);
        string cookie = login.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("kft_session=")).Split(';')[0];

        HttpClient onSecond = second.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        onSecond.DefaultRequestHeaders.Add("Cookie", cookie);
        HttpResponseMessage me = await onSecond.GetAsync("/api/auth/me");

        me.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
