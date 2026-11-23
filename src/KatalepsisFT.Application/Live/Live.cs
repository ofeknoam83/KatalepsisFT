using KatalepsisFT.Domain.Positions;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Live;

/// <summary>Tells whoever is looking at a fund that its figures have just changed.</summary>
public interface IFundChangeNotifier
{
    Task NotifyAsync(int fundId, CancellationToken cancellationToken);
}

/// <summary>For processes with nobody to tell.</summary>
public sealed class SilentFundChangeNotifier : IFundChangeNotifier
{
    public Task NotifyAsync(int fundId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <param name="Times">UTC, one per five-minute step, oldest first.</param>
public sealed record IntradayDto(IReadOnlyList<DateTimeOffset> Times, IReadOnlyList<decimal> NavUsd, IReadOnlyList<decimal> GrossUsd, IReadOnlyList<decimal> NetUsd);

/// <summary>Intraday samples: written as books change, read back as a fund-level series, pruned after ninety days.</summary>
public sealed class IntradayService(IFtDbContext database, TimeProvider clock)
{
    public static readonly TimeSpan Step = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    public const int MaxHours = 24 * 7;

    /// <summary>Keeps a sample of the manager's book unless one was kept within the last step.</summary>
    public async Task RecordAsync(int managerAccountId, decimal navUsd, decimal grossUsd, decimal netUsd, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        // A little under the step, so reads arriving a few seconds early do not skip every other sample.
        DateTimeOffset recent = now - Step + TimeSpan.FromSeconds(30);
        if (await database.NavSamples.AnyAsync(s => s.ManagerAccountId == managerAccountId && s.At > recent, cancellationToken))
        {
            return;
        }

        database.NavSamples.Add(new NavSample { ManagerAccountId = managerAccountId, At = now, NavUsd = navUsd, GrossUsd = grossUsd, NetUsd = netUsd });
        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The fund over the last hours on a five-minute grid. At each step every manager counts with its latest sample
    /// up to then, so managers read at slightly different moments still add up. Null when the fund does not exist.
    /// </summary>
    public async Task<IntradayDto?> GetAsync(int fundId, int hours, CancellationToken cancellationToken)
    {
        if (!await database.Funds.AnyAsync(f => f.Id == fundId, cancellationToken))
        {
            return null;
        }

        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset from = now - TimeSpan.FromHours(hours);
        // One step of lead-in, so a manager sampled just before the window still counts at its start.
        DateTimeOffset loadFrom = from - TimeSpan.FromHours(1);
        var samples = await database
            .NavSamples.AsNoTracking()
            .Where(s => s.ManagerAccount.Sleeve.FundId == fundId && s.At >= loadFrom)
            .OrderBy(s => s.At)
            .Select(s => new { s.ManagerAccountId, s.At, s.NavUsd, s.GrossUsd, s.NetUsd })
            .ToListAsync(cancellationToken);
        if (samples.Count == 0)
        {
            return new IntradayDto([], [], [], []);
        }

        long stepTicks = Step.Ticks;
        DateTimeOffset first = samples[0].At < from ? from : samples[0].At;
        DateTimeOffset cursor = new(first.UtcTicks / stepTicks * stepTicks + stepTicks, TimeSpan.Zero);
        List<DateTimeOffset> times = [];
        List<decimal> nav = [],
            gross = [],
            net = [];
        Dictionary<int, (decimal Nav, decimal Gross, decimal Net)> latest = [];
        int next = 0;
        for (; cursor <= now + Step; cursor += Step)
        {
            while (next < samples.Count && samples[next].At <= cursor)
            {
                latest[samples[next].ManagerAccountId] = (samples[next].NavUsd, samples[next].GrossUsd, samples[next].NetUsd);
                next++;
            }

            if (latest.Count == 0)
            {
                continue;
            }

            times.Add(cursor);
            nav.Add(latest.Values.Sum(v => v.Nav));
            gross.Add(latest.Values.Sum(v => v.Gross));
            net.Add(latest.Values.Sum(v => v.Net));
            if (next >= samples.Count && cursor >= now)
            {
                break;
            }
        }

        return new IntradayDto(times, nav, gross, net);
    }

    /// <returns>How many samples were removed.</returns>
    public async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = clock.GetUtcNow() - Retention;
        // Run daily, so this is one day's worth of rows.
        List<NavSample> old = await database.NavSamples.Where(s => s.At < cutoff).ToListAsync(cancellationToken);
        database.NavSamples.RemoveRange(old);
        await database.SaveChangesAsync(cancellationToken);
        return old.Count;
    }
}
