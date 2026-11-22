using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using KatalepsisFT.Application.Alerts;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Holdings;
using KatalepsisFT.Application.Risk;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Domain.Alerts;
using KatalepsisFT.Web.Alerts;
using KatalepsisFT.Web.Tests.Demo;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KatalepsisFT.Web.Tests.Funds;

/// <summary>
/// Rules that follow the mandate are compared with the Risk page's mandate checks. The workflow tests use a rule of
/// their own that always holds (gross exposure above zero), so each has an alert nobody else touches.
/// </summary>
public class AlertsApiTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private HttpClient _admin = null!;
    private HttpClient _viewer = null!;
    private int _fundId;

    public async Task InitializeAsync()
    {
        WebApplicationFactory<Program> app = database.App;
        if (!await database.IsSeededAsync(app))
        {
            await DemoTestData.SeedAsync(app);
        }

        _admin = await SignInAsync(Roles.Admin);
        _viewer = await SignInAsync(Roles.Viewer);
        _fundId = (await Get<List<FundListItemDto>>("/api/funds")).Single().Id;
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

    private async Task<T> Post<T>(string url, object? body = null)
    {
        HttpResponseMessage response = await _admin.PostAsJsonAsync(url, body ?? new { }, Json);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private SaveAlertRuleRequest AlwaysTrue(AlertSeverity severity = AlertSeverity.Warning) =>
        new(AlertScopeKind.Fund, _fundId, AlertMetric.GrossExposureOverNav, AlertOperator.Above, 0m, severity, true);

    /// <summary>A rule that holds, and the alert it raised.</summary>
    private async Task<(AlertRuleDto Rule, AlertDto Alert)> RaiseAsync()
    {
        AlertRuleDto rule = await Post<AlertRuleDto>($"/api/funds/{_fundId}/alert-rules", AlwaysTrue());
        AlertDto alert = (await Get<AlertListDto>($"/api/funds/{_fundId}/alerts")).Alerts.Single(a => a.RuleId == rule.Id);
        return (rule, alert);
    }

    [Fact]
    public async Task Evaluate_RaisesOneAlertPerBreachedMandateLimit_AndNotAgain()
    {
        await Post<EvaluationResultDto>($"/api/funds/{_fundId}/alerts/evaluate");
        EvaluationResultDto second = await Post<EvaluationResultDto>($"/api/funds/{_fundId}/alerts/evaluate");

        RiskDto risk = await Get<RiskDto>($"/api/risk/Fund/{_fundId}");
        AlertRulesDto rules = await Get<AlertRulesDto>($"/api/funds/{_fundId}/alert-rules");
        List<AlertDto> mandateAlerts = (await Get<AlertListDto>($"/api/funds/{_fundId}/alerts?state=All")).Alerts.Where(a => a.FromMandate).ToList();

        second.Raised.Should().Be(0);
        rules.Rules.Count(r => r.FromMandate).Should().Be(risk.Mandate.Count);
        rules.Rules.Where(r => r.FromMandate).Should().OnlyContain(r => r.Severity == AlertSeverity.Critical && r.ScopeKind == AlertScopeKind.Sleeve);
        mandateAlerts.Should().HaveCount(risk.Mandate.Count(m => m.WithinLimit == false));
        mandateAlerts.Should().OnlyContain(a => a.Status == AlertStatus.Open && a.DataDate == DemoTestData.LastDay && a.Actions.Single().Kind == AlertActionKind.Raised);
        foreach (AlertDto alert in mandateAlerts)
        {
            risk.Mandate.Should().Contain(m => m.SleeveId == alert.ScopeId && m.Current == alert.Value && m.Limit == alert.Threshold && m.WithinLimit == false);
        }
    }

    [Fact]
    public async Task NewRuleThatHolds_RaisesAtOnce_AndRecoversWhenTheThresholdMoves()
    {
        (AlertRuleDto rule, AlertDto alert) = await RaiseAsync();

        alert.Status.Should().Be(AlertStatus.Open);
        alert.ScopeName.Should().Be("Liquid Master Fund");
        alert.Description.Should().StartWith("Gross exposure ÷ NAV is ").And.EndWith("above 0.0%");

        HttpResponseMessage update = await _admin.PutAsJsonAsync($"/api/alert-rules/{rule.Id}", AlwaysTrue() with { Threshold = 9m }, Json);
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        AlertDto after = (await Get<AlertListDto>($"/api/funds/{_fundId}/alerts?state=Resolved")).Alerts.Single(a => a.Id == alert.Id);

        after.Status.Should().Be(AlertStatus.Resolved);
        after.Actions.Select(a => a.Kind).Should().Equal(AlertActionKind.Raised, AlertActionKind.Recovered);
        after.Actions[^1].UserName.Should().BeNull();
    }

    [Fact]
    public async Task Workflow_AcknowledgeAssignCommentSnooze_IsRecordedInOrder()
    {
        (_, AlertDto alert) = await RaiseAsync();
        AssigneeDto me = (await Get<List<AssigneeDto>>("/api/alerts/assignees"))[0];

        AlertDto acknowledged = await Post<AlertDto>($"/api/alerts/{alert.Id}/acknowledge");
        (await _admin.PostAsJsonAsync($"/api/alerts/{alert.Id}/acknowledge", new { })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        AlertDto assigned = await Post<AlertDto>($"/api/alerts/{alert.Id}/assign", new AssignRequest(me.Id));
        await Post<AlertDto>($"/api/alerts/{alert.Id}/comment", new NoteRequest("Spoke to the manager."));
        AlertDto snoozed = await Post<AlertDto>($"/api/alerts/{alert.Id}/snooze", new SnoozeRequest(3, null));

        acknowledged.Status.Should().Be(AlertStatus.Acknowledged);
        assigned.AssigneeUserId.Should().Be(me.Id);
        assigned.AssigneeName.Should().Be(me.Name);
        snoozed.IsSnoozed.Should().BeTrue();
        snoozed.SnoozedUntil.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(3), TimeSpan.FromMinutes(1));
        snoozed.Actions.Select(a => a.Kind).Should().Equal(AlertActionKind.Raised, AlertActionKind.Acknowledged, AlertActionKind.Assigned, AlertActionKind.Commented, AlertActionKind.Snoozed);
        snoozed.Actions[3].Note.Should().Be("Spoke to the manager.");
        snoozed.Actions.Skip(1).Should().OnlyContain(a => a.UserName == "Test User");
        AlertListDto list = await Get<AlertListDto>($"/api/funds/{_fundId}/alerts");
        list.Alerts.Should().Contain(a => a.Id == alert.Id);
        list.Counts.Snoozed.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ResolvedByHand_IsNotRaisedAgainWhileTheConditionStillHolds()
    {
        (AlertRuleDto rule, AlertDto alert) = await RaiseAsync();

        AlertDto resolved = await Post<AlertDto>($"/api/alerts/{alert.Id}/resolve", new NoteRequest("Accepted by the committee."));
        await Post<EvaluationResultDto>($"/api/funds/{_fundId}/alerts/evaluate");
        List<AlertDto> ofRule = (await Get<AlertListDto>($"/api/funds/{_fundId}/alerts?state=All")).Alerts.Where(a => a.RuleId == rule.Id).ToList();

        resolved.Status.Should().Be(AlertStatus.Resolved);
        resolved.Actions[^1].Note.Should().Be("Accepted by the committee.");
        ofRule.Should().ContainSingle().Which.Status.Should().Be(AlertStatus.Resolved);
        (await _admin.PostAsJsonAsync($"/api/alerts/{alert.Id}/snooze", new SnoozeRequest(1, null))).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ResolvedByHand_RaisesAgainOnceTheConditionHasGoneAwayAndComeBack()
    {
        (AlertRuleDto rule, AlertDto alert) = await RaiseAsync();
        await Post<AlertDto>($"/api/alerts/{alert.Id}/resolve", new NoteRequest(null));

        await _admin.PutAsJsonAsync($"/api/alert-rules/{rule.Id}", AlwaysTrue() with { Threshold = 9m }, Json);
        await _admin.PutAsJsonAsync($"/api/alert-rules/{rule.Id}", AlwaysTrue(), Json);
        List<AlertDto> ofRule = (await Get<AlertListDto>($"/api/funds/{_fundId}/alerts?state=All")).Alerts.Where(a => a.RuleId == rule.Id).ToList();

        ofRule.Should().HaveCount(2);
        ofRule.Should().ContainSingle(a => a.Status == AlertStatus.Open);
    }

    [Fact]
    public async Task SwitchingARuleOff_ClosesItsAlert()
    {
        (AlertRuleDto rule, AlertDto alert) = await RaiseAsync();

        await _admin.PutAsJsonAsync($"/api/alert-rules/{rule.Id}", AlwaysTrue() with { IsEnabled = false }, Json);
        AlertDto after = (await Get<AlertListDto>($"/api/funds/{_fundId}/alerts?state=Resolved")).Alerts.Single(a => a.Id == alert.Id);

        after.Actions[^1].Kind.Should().Be(AlertActionKind.Recovered);
        after.Actions[^1].Note.Should().Be(AlertEvaluator.RuleSwitchedOff);
    }

    [Fact]
    public async Task MandateRule_KeepsItsCondition_AndCannotBeDeleted()
    {
        await Post<EvaluationResultDto>($"/api/funds/{_fundId}/alerts/evaluate");
        AlertRuleDto mandate = (await Get<AlertRulesDto>($"/api/funds/{_fundId}/alert-rules")).Rules.First(r => r.FromMandate);

        HttpResponseMessage update = await _admin.PutAsJsonAsync($"/api/alert-rules/{mandate.Id}", AlwaysTrue(AlertSeverity.Warning), Json);
        AlertRuleDto updated = (await update.Content.ReadFromJsonAsync<AlertRuleDto>(Json))!;
        HttpResponseMessage delete = await _admin.DeleteAsync($"/api/alert-rules/{mandate.Id}");

        updated.Severity.Should().Be(AlertSeverity.Warning);
        updated.Should().BeEquivalentTo(mandate, o => o.Excluding(r => r.Severity).Excluding(r => r.HasActiveAlert));
        delete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await _admin.PutAsJsonAsync($"/api/alert-rules/{mandate.Id}", AlwaysTrue(AlertSeverity.Critical), Json);
    }

    [Fact]
    public async Task CustomRule_CanBeDeleted_WithItsAlerts()
    {
        (AlertRuleDto rule, AlertDto alert) = await RaiseAsync();

        (await _admin.DeleteAsync($"/api/alert-rules/{rule.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Get<AlertListDto>($"/api/funds/{_fundId}/alerts?state=All")).Alerts.Should().NotContain(a => a.Id == alert.Id);
        (await _admin.DeleteAsync($"/api/alert-rules/{rule.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SleeveFilter_ShowsTheSleeveAndItsManagersOnly()
    {
        await Post<EvaluationResultDto>($"/api/funds/{_fundId}/alerts/evaluate");
        AlertRulesDto rules = await Get<AlertRulesDto>($"/api/funds/{_fundId}/alert-rules");
        AlertScopeOptionDto sleeve = rules.Scopes.First(s => s.Kind == AlertScopeKind.Sleeve);
        await RaiseAsync();
        await Post<AlertRuleDto>($"/api/funds/{_fundId}/alert-rules", AlwaysTrue() with { ScopeKind = AlertScopeKind.Sleeve, ScopeId = sleeve.Id });

        AlertListDto filtered = await Get<AlertListDto>($"/api/funds/{_fundId}/alerts?sleeveId={sleeve.Id}");

        filtered.Alerts.Should().NotBeEmpty().And.OnlyContain(a => a.SleeveId == sleeve.Id);
        filtered.Counts.Active.Should().Be(filtered.Alerts.Count);
    }

    [Fact]
    public async Task Rule_WithAThresholdOutOfRange_OrAScopeOutsideTheFund_IsRefused()
    {
        HttpResponseMessage tooLarge = await _admin.PostAsJsonAsync($"/api/funds/{_fundId}/alert-rules", AlwaysTrue() with { Threshold = 100m }, Json);
        HttpResponseMessage elsewhere = await _admin.PostAsJsonAsync($"/api/funds/{_fundId}/alert-rules", AlwaysTrue() with { ScopeKind = AlertScopeKind.Manager, ScopeId = 999_999 }, Json);
        HttpResponseMessage noFund = await _admin.PostAsJsonAsync("/api/funds/999999/alert-rules", AlwaysTrue(), Json);

        tooLarge.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        elsewhere.StatusCode.Should().Be(HttpStatusCode.Conflict);
        noFund.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Viewer_CanRead_ButNotActOrChangeRules()
    {
        (AlertRuleDto rule, AlertDto alert) = await RaiseAsync();

        (await _viewer.GetAsync($"/api/funds/{_fundId}/alerts")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _viewer.GetAsync($"/api/funds/{_fundId}/alert-rules")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _viewer.PostAsJsonAsync($"/api/alerts/{alert.Id}/acknowledge", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _viewer.PostAsJsonAsync($"/api/alerts/{alert.Id}/resolve", new NoteRequest(null))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _viewer.PostAsJsonAsync($"/api/funds/{_fundId}/alert-rules", AlwaysTrue(), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _viewer.DeleteAsync($"/api/alert-rules/{rule.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _viewer.PostAsJsonAsync($"/api/funds/{_fundId}/alerts/evaluate", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _viewer.GetAsync("/api/alerts/assignees")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UnknownAlert_IsNotFound_AndSnoozeLengthIsChecked()
    {
        (_, AlertDto alert) = await RaiseAsync();

        (await _admin.PostAsJsonAsync("/api/alerts/999999/acknowledge", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _admin.PostAsJsonAsync($"/api/alerts/{alert.Id}/snooze", new SnoozeRequest(0, null))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _admin.PostAsJsonAsync($"/api/alerts/{alert.Id}/comment", new NoteRequest(" "))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _admin.PostAsJsonAsync($"/api/alerts/{alert.Id}/assign", new AssignRequest(999_999))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _admin.GetAsync("/api/funds/999999/alerts")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
