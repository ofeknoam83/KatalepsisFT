using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KatalepsisFT.Web.Tests;

public class HealthEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_Always_Returns200()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_WithNoStoresConfigured_Returns200AndSaysNotConfigured()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/ready");
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("postgres").GetString().Should().Be("not configured");
        body.GetProperty("keyValue").GetString().Should().Be("not configured");
        body.GetProperty("workerHeartbeatAgeSeconds").GetProperty("core").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Ready_WhenPostgresIsUnreachable_Returns503()
    {
        HttpClient client = factory
            .WithWebHostBuilder(builder => builder.UseSetting("DATABASE_URL", "postgresql://u:p@127.0.0.1:1/none"))
            .CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/ready");
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.GetProperty("postgres").GetString().Should().StartWith("failed");
    }
}
