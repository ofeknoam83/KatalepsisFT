using FluentAssertions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Venues.Deribit;
using KatalepsisFT.Venues.Okx;

namespace KatalepsisFT.Venues.Tests;

/// <summary>
/// Calls the real venues with a key that does not exist. Off unless LIVE_VENUE_CHECKS=1, because it needs the internet
/// and the venues to be reachable from where the tests run.
/// </summary>
public class LiveVenueChecks
{
    private static readonly VenueCredentials Nonsense = new("0000000000000000", "0000000000000000000000000000000000000000000", "Not-a-real-passphrase-1");

    private static bool Enabled => Environment.GetEnvironmentVariable("LIVE_VENUE_CHECKS") == "1";

    [Fact]
    public async Task Deribit_RefusesAKeyThatDoesNotExist()
    {
        if (!Enabled)
        {
            return;
        }

        DeribitAdapter adapter = new(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, TimeProvider.System);

        VenueException e = (await FluentActions.Invoking(() => adapter.CheckCredentialsAsync(Nonsense, CancellationToken.None)).Should().ThrowAsync<VenueException>()).Which;

        e.Failure.Should().Be(VenueFailure.Rejected, e.Message);
    }

    [Fact]
    public async Task Okx_RefusesAKeyThatDoesNotExist()
    {
        if (!Enabled)
        {
            return;
        }

        VenueException e = (await FluentActions.Invoking(() => new OkxAdapter(TimeProvider.System).CheckCredentialsAsync(Nonsense, CancellationToken.None)).Should().ThrowAsync<VenueException>()).Which;

        e.Failure.Should().Be(VenueFailure.Rejected, e.Message);
    }
}
