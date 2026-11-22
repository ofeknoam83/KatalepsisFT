using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace KatalepsisFT.Web.Tests;

public class HealthEndpointTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    [Fact]
    public async Task Health_WithoutSignIn_Returns200()
    {
        HttpClient client = TestDatabase.CreateClient(database.CreateApp());

        HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_WhenPostgresAnswers_Returns200AndNoDetail()
    {
        HttpClient client = TestDatabase.CreateClient(database.CreateApp());

        HttpResponseMessage response = await client.GetAsync("/health/ready");
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("ready").GetBoolean().Should().BeTrue();
        body.EnumerateObject().Select(p => p.Name).Should().Equal("ready");
    }

    [Fact]
    public async Task Ready_WhenPostgresIsUnreachable_Returns503()
    {
        HttpClient client = TestDatabase.CreateClient(database.CreateApp("postgresql://u:p@127.0.0.1:1/none"));

        HttpResponseMessage response = await client.GetAsync("/health/ready");
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.GetProperty("ready").GetBoolean().Should().BeFalse();
    }
}
