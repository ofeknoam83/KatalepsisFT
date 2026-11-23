using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using KatalepsisFT.Application.Alerts;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Holdings;
using KatalepsisFT.Application.Structure;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Web.Tests.Demo;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KatalepsisFT.Web.Tests.Structure;

public class StructureApiTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        WebApplicationFactory<Program> app = database.App;
        if (!await database.IsSeededAsync(app))
        {
            await DemoTestData.SeedAsync(app);
        }

        _admin = await SignInAsync(Roles.Admin);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> SignInAsync(string role)
    {
        string email = await database.AddUserAsync(database.App, role);
        HttpClient client = TestDatabase.CreateClient(database.App);
        await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, TestDatabase.Password, false));
        return client;
    }

    private async Task<T> Get<T>(string url) => (await _admin.GetFromJsonAsync<T>(url, Json))!;

    private async Task<StructureDto> NewFundAsync()
    {
        NewFundRequest request = new($"Fund {Guid.NewGuid():N}", null, [new NewSleeveRequest("Directional", MandateType.Directional, [new NewManagerRequest("Alpha Desk", 1_000_000m)])]);
        int fundId = (await (await _admin.PostAsJsonAsync("/api/funds", request, Json)).Content.ReadFromJsonAsync<FundCreatedDto>(Json))!.Id;
        return await Get<StructureDto>($"/api/funds/{fundId}/structure");
    }

    private async Task<int> DemoFundIdAsync() => (await Get<List<FundListItemDto>>("/api/funds")).Single(f => f.IsDemo).Id;

    [Fact]
    public async Task Fund_CanBeRenamed_AndGivenABenchmark()
    {
        StructureDto fund = await NewFundAsync();
        string name = $"Renamed {Guid.NewGuid():N}";

        HttpResponseMessage response = await _admin.PutAsJsonAsync($"/api/funds/{fund.Id}", new SaveFundRequest(name, fund.Benchmarks[0].Id), Json);
        StructureDto after = await Get<StructureDto>($"/api/funds/{fund.Id}/structure");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        after.Name.Should().Be(name);
        after.BenchmarkId.Should().Be(fund.Benchmarks[0].Id);
        after.CanEdit.Should().BeTrue();
        (await _admin.PutAsJsonAsync($"/api/funds/{fund.Id}", new SaveFundRequest(name, 999_999), Json)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _admin.PutAsJsonAsync($"/api/funds/{fund.Id}", new SaveFundRequest(" ", null), Json)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sleeves_CanBeAddedRenamedAndRemovedWhileEmpty()
    {
        StructureDto fund = await NewFundAsync();

        (await _admin.PostAsJsonAsync($"/api/funds/{fund.Id}/sleeves", new SaveSleeveRequest("Beta", MandateType.Beta, null), Json)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _admin.PostAsJsonAsync($"/api/funds/{fund.Id}/sleeves", new SaveSleeveRequest("beta", MandateType.Beta, null), Json)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        StructureSleeveDto beta = (await Get<StructureDto>($"/api/funds/{fund.Id}/structure")).Sleeves.Single(s => s.Name == "Beta");
        (await _admin.PutAsJsonAsync($"/api/sleeves/{beta.Id}", new SaveSleeveRequest("Core Beta", MandateType.Other, null), Json)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        StructureDto renamed = await Get<StructureDto>($"/api/funds/{fund.Id}/structure");

        renamed.Sleeves.Select(s => s.Name).Should().Equal("Directional", "Core Beta");
        renamed.Sleeves[1].MandateType.Should().Be(MandateType.Other);
        (await _admin.DeleteAsync($"/api/sleeves/{fund.Sleeves[0].Id}")).StatusCode.Should().Be(HttpStatusCode.Conflict, "it still has a manager");
        (await _admin.DeleteAsync($"/api/sleeves/{beta.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Get<StructureDto>($"/api/funds/{fund.Id}/structure")).Sleeves.Should().ContainSingle();
    }

    [Fact]
    public async Task MandateLimits_AreReplacedAsASet_AndAlertRulesFollow()
    {
        StructureDto fund = await NewFundAsync();
        int sleeveId = fund.Sleeves[0].Id;
        SaveSleeveRequest first = new("Directional", MandateType.Directional, [new LimitDto(MandateLimitKind.GrossExposureMax, 2m), new LimitDto(MandateLimitKind.SingleAssetMax, 0.15m)]);
        SaveSleeveRequest second = new("Directional", MandateType.Directional, [new LimitDto(MandateLimitKind.SingleAssetMax, 0.20m), new LimitDto(MandateLimitKind.DaysToLiquidateMax, 5m)]);

        (await _admin.PutAsJsonAsync($"/api/sleeves/{sleeveId}", first, Json)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        List<AlertRuleDto> rulesBefore = (await Get<AlertRulesDto>($"/api/funds/{fund.Id}/alert-rules")).Rules.ToList();
        (await _admin.PutAsJsonAsync($"/api/sleeves/{sleeveId}", second, Json)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        StructureDto after = await Get<StructureDto>($"/api/funds/{fund.Id}/structure");
        List<AlertRuleDto> rulesAfter = (await Get<AlertRulesDto>($"/api/funds/{fund.Id}/alert-rules")).Rules.ToList();

        after.Sleeves[0].Limits.Should().Equal(new LimitDto(MandateLimitKind.SingleAssetMax, 0.20m), new LimitDto(MandateLimitKind.DaysToLiquidateMax, 5m));
        rulesBefore.Should().HaveCount(2).And.OnlyContain(r => r.FromMandate);
        rulesAfter.Should().HaveCount(2);
        // The limit that stayed keeps its rule; only its threshold moved.
        AlertRuleDto single = rulesAfter.Single(r => r.Metric == Domain.Alerts.AlertMetric.LargestAssetOverNav);
        single.Id.Should().Be(rulesBefore.Single(r => r.Metric == Domain.Alerts.AlertMetric.LargestAssetOverNav).Id);
        single.Threshold.Should().Be(0.20m);
        rulesAfter.Should().NotContain(r => r.Metric == Domain.Alerts.AlertMetric.GrossExposureOverNav);
    }

    [Theory]
    [InlineData(MandateLimitKind.ManagerDrawdownMin, 0.25)]
    [InlineData(MandateLimitKind.GrossExposureMax, -1)]
    [InlineData(MandateLimitKind.DaysToLiquidateMax, 5000)]
    public async Task LimitOutsideItsRange_IsRefused(MandateLimitKind kind, double value)
    {
        StructureDto fund = await NewFundAsync();

        HttpResponseMessage response = await _admin.PutAsJsonAsync($"/api/sleeves/{fund.Sleeves[0].Id}", new SaveSleeveRequest("Directional", MandateType.Directional, [new LimitDto(kind, (decimal)value)]), Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NetExposureBand_MustNotBeUpsideDown_AndALimitCannotAppearTwice()
    {
        StructureDto fund = await NewFundAsync();
        string url = $"/api/sleeves/{fund.Sleeves[0].Id}";

        HttpResponseMessage upsideDown = await _admin.PutAsJsonAsync(url, new SaveSleeveRequest("Directional", MandateType.Directional, [new LimitDto(MandateLimitKind.NetExposureMin, 0.1m), new LimitDto(MandateLimitKind.NetExposureMax, -0.1m)]), Json);
        HttpResponseMessage twice = await _admin.PutAsJsonAsync(url, new SaveSleeveRequest("Directional", MandateType.Directional, [new LimitDto(MandateLimitKind.SingleAssetMax, 0.1m), new LimitDto(MandateLimitKind.SingleAssetMax, 0.2m)]), Json);

        upsideDown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        twice.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Managers_CanBeAddedChangedAndRemovedBeforeTheyHaveHistory()
    {
        StructureDto fund = await NewFundAsync();
        int sleeveId = fund.Sleeves[0].Id;

        (await _admin.PostAsJsonAsync($"/api/sleeves/{sleeveId}/managers", new SaveManagerRequest("Beta Desk", 2_000_000m), Json)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        StructureManagerDto added = (await Get<StructureDto>($"/api/funds/{fund.Id}/structure")).Sleeves[0].Managers.Single(m => m.Name == "Beta Desk");
        (await _admin.PutAsJsonAsync($"/api/managers/{added.Id}", new SaveManagerRequest("Gamma Desk", 3_000_000m), Json)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        StructureManagerDto changed = (await Get<StructureDto>($"/api/funds/{fund.Id}/structure")).Sleeves[0].Managers.Single(m => m.Id == added.Id);

        changed.Should().Be(new StructureManagerDto(added.Id, "Gamma Desk", 3_000_000m, ManagerDataSource.Venue, false, 0));
        (await _admin.PutAsJsonAsync($"/api/managers/{added.Id}", new SaveManagerRequest("", -1m), Json)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _admin.DeleteAsync($"/api/managers/{added.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _admin.DeleteAsync($"/api/managers/{added.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ExampleFund_IsShown_ButCannotBeEdited()
    {
        StructureDto demo = await Get<StructureDto>($"/api/funds/{await DemoFundIdAsync()}/structure");
        StructureSleeveDto sleeve = demo.Sleeves[0];

        demo.CanEdit.Should().BeFalse();
        demo.Sleeves.Should().HaveCount(3);
        sleeve.Limits.Should().NotBeEmpty();
        sleeve.Managers.Should().OnlyContain(m => m.HasHistory && m.DataSource == ManagerDataSource.Demo);
        (await _admin.PutAsJsonAsync($"/api/funds/{demo.Id}", new SaveFundRequest("Hijacked", null), Json)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _admin.PutAsJsonAsync($"/api/sleeves/{sleeve.Id}", new SaveSleeveRequest("Hijacked", MandateType.Other, []), Json)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _admin.PostAsJsonAsync($"/api/sleeves/{sleeve.Id}/managers", new SaveManagerRequest("Intruder", 0m), Json)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _admin.DeleteAsync($"/api/managers/{sleeve.Managers[0].Id}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Get<StructureDto>($"/api/funds/{demo.Id}/structure")).Should().BeEquivalentTo(demo);
    }

    [Fact]
    public async Task Viewer_CannotSeeOrChangeStructure()
    {
        StructureDto fund = await NewFundAsync();
        HttpClient viewer = await SignInAsync(Roles.Viewer);

        (await viewer.GetAsync($"/api/funds/{fund.Id}/structure")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PutAsJsonAsync($"/api/funds/{fund.Id}", new SaveFundRequest("x", null), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PutAsJsonAsync($"/api/sleeves/{fund.Sleeves[0].Id}", new SaveSleeveRequest("x", MandateType.Other, null), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.DeleteAsync($"/api/managers/{fund.Sleeves[0].Managers[0].Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.GetAsync($"/api/sleeves/{fund.Sleeves[0].Id}")).StatusCode.Should().Be(HttpStatusCode.OK, "reading a sleeve's holdings stays open to everyone signed in");
        (await _admin.GetAsync("/api/funds/999999/structure")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
