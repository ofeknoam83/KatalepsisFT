using System.Net;
using System.Text;
using FluentAssertions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Venues.Rest;
using Xunit;

namespace KatalepsisFT.Venues.Tests;

/// <summary>A part of an account that fails to answer must fail the read; only the venue saying "no" may be read as "no such part".</summary>
public class PartialReadTests
{
    private static readonly VenueCredentials Credentials = new(new string('0', 32), new string('0', 64));

    private sealed class Stub(Func<string, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            (HttpStatusCode status, string body) = respond(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static GateAdapter Gate(HttpStatusCode futuresStatus, string futuresBody) =>
        new(
            new HttpClient(
                new Stub(path =>
                    path.EndsWith("/spot/accounts") ? (HttpStatusCode.OK, """[{"currency":"USDT","available":"1000","locked":"0"}]""")
                    : path.EndsWith("/spot/tickers") ? (HttpStatusCode.OK, "[]")
                    : (futuresStatus, futuresBody)
                )
            ),
            TimeProvider.System
        );

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, """{"label":"TOO_MANY_REQUESTS","message":"slow down"}""")]
    [InlineData(HttpStatusCode.BadGateway, "<html>bad gateway</html>")]
    [InlineData(HttpStatusCode.InternalServerError, """{"label":"SERVER_ERROR","message":"try later"}""")]
    public async Task Gate_FuturesThatDoNotAnswer_FailTheWholeRead(HttpStatusCode status, string body)
    {
        VenueException e = (await FluentActions.Invoking(() => Gate(status, body).GetSnapshotAsync(Credentials, CancellationToken.None)).Should().ThrowAsync<VenueException>()).Which;

        e.Failure.Should().Be(VenueFailure.Unavailable);
    }

    [Fact]
    public async Task Gate_AccountWithoutFutures_IsReadWithANote()
    {
        VenueSnapshot snapshot = await Gate(HttpStatusCode.BadRequest, """{"label":"USER_NOT_FOUND","message":"please transfer funds first to create futures account"}""").GetSnapshotAsync(Credentials, CancellationToken.None);

        snapshot.Positions.Should().ContainSingle().Which.Quantity.Should().Be(1_000m);
        snapshot.Notes.Should().ContainSingle().Which.Should().StartWith("The futures account was not read:");
    }
}
