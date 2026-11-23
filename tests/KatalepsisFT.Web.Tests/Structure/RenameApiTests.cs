using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using KatalepsisFT.Application.Audit;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Structure;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Domain.Funds;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KatalepsisFT.Web.Tests.Structure;

/// <summary>Changing what a fund, its owner, a sleeve or a manager is called, without touching anything else about it.</summary>
public class RenameApiTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private async Task<HttpClient> SignInAsync(string role)
    {
        WebApplicationFactory<Program> app = database.App;
        string email = await database.AddUserAsync(app, role);
        HttpClient client = TestDatabase.CreateClient(app);
        await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, TestDatabase.Password, false));
        return client;
    }

    private static async Task<StructureDto> NewFundAsync(HttpClient admin, string name)
    {
        NewFundRequest request = new(name, null, [new NewSleeveRequest("Core", MandateType.Directional, [new NewManagerRequest("Desk", 250_000m)]), new NewSleeveRequest("Hedge", MandateType.MarketNeutral, [new NewManagerRequest("Other", 0m)])], FundKind.Sma, "First Owner");
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/funds", request, Json);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        int id = (await created.Content.ReadFromJsonAsync<FundCreatedDto>(Json))!.Id;
        return (await admin.GetFromJsonAsync<StructureDto>($"/api/funds/{id}/structure", Json))!;
    }

    [Fact]
    public async Task Admin_RenamesAFundItsOwnerASleeveAndAManager_AndNothingElseChanges()
    {
        HttpClient admin = await SignInAsync(Roles.Admin);
        StructureDto fund = await NewFundAsync(admin, $"Before {Guid.NewGuid():N}");
        string renamed = $"After {Guid.NewGuid():N}";

        (await admin.PostAsJsonAsync($"/api/funds/{fund.Id}/name", new RenameRequest($"  {renamed} "))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.PostAsJsonAsync($"/api/funds/{fund.Id}/owner", new RenameRequest("Second Owner"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.PostAsJsonAsync($"/api/sleeves/{fund.Sleeves[0].Id}/name", new RenameRequest("Main book"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.PostAsJsonAsync($"/api/managers/{fund.Sleeves[0].Managers[0].Id}/name", new RenameRequest("Alpha Desk"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        StructureDto after = (await admin.GetFromJsonAsync<StructureDto>($"/api/funds/{fund.Id}/structure", Json))!;
        AuditListDto trail = (await admin.GetFromJsonAsync<AuditListDto>($"/api/audit?fundId={fund.Id}", Json))!;

        after.Name.Should().Be(renamed);
        after.OwnerName.Should().Be("Second Owner");
        after.Kind.Should().Be(FundKind.Sma);
        after.Sleeves[0].Name.Should().Be("Main book");
        after.Sleeves[0].MandateType.Should().Be(MandateType.Directional);
        after.Sleeves[0].Managers[0].Name.Should().Be("Alpha Desk");
        after.Sleeves[0].Managers[0].AllocatedCapitalUsd.Should().Be(250_000m);
        after.Sleeves[1].Name.Should().Be("Hedge");
        // The trail names the thing as it was called and says what it is called now.
        trail.Entries.Should().Contain(e => e.Action == "Sleeve renamed" && e.Subject == "Core" && e.Detail == "name: Main book");
        trail.Entries.Select(e => e.Action).Should().Contain(["Fund renamed", "Account owner's name changed", "Manager renamed"]);
    }

    [Fact]
    public async Task NameThatIsEmptyTooLongOrTaken_IsRefused_AndAViewerCannotRename()
    {
        HttpClient admin = await SignInAsync(Roles.Admin);
        HttpClient viewer = await SignInAsync(Roles.Viewer);
        StructureDto first = await NewFundAsync(admin, $"One {Guid.NewGuid():N}");
        StructureDto second = await NewFundAsync(admin, $"Two {Guid.NewGuid():N}");

        HttpResponseMessage taken = await admin.PostAsJsonAsync($"/api/funds/{second.Id}/name", new RenameRequest(first.Name.ToUpperInvariant()));
        HttpResponseMessage sameSleeve = await admin.PostAsJsonAsync($"/api/sleeves/{first.Sleeves[1].Id}/name", new RenameRequest("core"));

        taken.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await taken.Content.ReadFromJsonAsync<ErrorResponse>(Json))!.Message.Should().Contain("already exists");
        sameSleeve.StatusCode.Should().Be(HttpStatusCode.Conflict);
        // Keeping its own name, or the same sleeve name in another fund, is fine.
        (await admin.PostAsJsonAsync($"/api/funds/{first.Id}/name", new RenameRequest(first.Name))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.PostAsJsonAsync($"/api/sleeves/{second.Sleeves[1].Id}/name", new RenameRequest("Core 2"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.PostAsJsonAsync($"/api/managers/{first.Sleeves[0].Managers[0].Id}/name", new RenameRequest("   "))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync($"/api/managers/{first.Sleeves[0].Managers[0].Id}/name", new RenameRequest(new string('x', 201)))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync("/api/managers/999999/name", new RenameRequest("Nobody"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await viewer.PostAsJsonAsync($"/api/funds/{first.Id}/name", new RenameRequest("Mine now"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.GetFromJsonAsync<StructureDto>($"/api/funds/{first.Id}/structure", Json))!.Name.Should().Be(first.Name);
    }
}
