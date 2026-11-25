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

/// <param name="Times">UTC, one per step, oldest first: ten seconds apart for a window of up to an hour, five minutes apart for a longer one.</param>
public sealed record IntradayDto(IReadOnlyList<DateTimeOffset> Times, IReadOnlyList<decimal> NavUsd, IReadOnlyList<decimal> GrossUsd, IReadOnlyList<decimal> NetUsd);

/// <summary>
/// Intraday samples: written as books change, about every ten seconds, and read back as a fund-level series. The last
/// day is kept in full; older than that, one sample per five minutes is kept, for ninety days.
/// </summary>
public sealed class IntradayService(IFtDbContext database, TimeProvider clock)
{
    /// <summary>How often a sample is kept, and the step of a window of up to <see cref="FineHours"/>.</summary>
    public static readonly TimeSpan FineStep = TimeSpan.FromSeconds(10);

    /// <summary>The step of a longer window, and how far apart samples older than <see cref="FineKept"/> are left.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromMinutes(5);

    /// <summary>A window this long or shorter is read in ten-second steps: 360 points an hour.</summary>
    public const int FineHours = 1;

    /// <summary>How long every ten-second sample is kept before it is thinned.</summary>
    public static readonly TimeSpan FineKept = TimeSpan.FromHours(24);

    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    public const int MaxHours = 24 * 7;

    /// <summary>Keeps a sample of the manager's book unless one was kept within the last step.</summary>
    public async Task RecordAsync(int managerAccountId, decimal navUsd, decimal grossUsd, decimal netUsd, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        // A little under the step, so reads arriving a moment early do not skip every other sample.
        DateTimeOffset recent = now - FineStep + TimeSpan.FromSeconds(2);
        if (await database.NavSamples.AnyAsync(s => s.ManagerAccountId == managerAccountId && s.At > recent, cancellationToken))
        {
            return;
        }

        database.NavSamples.Add(new NavSample { ManagerAccountId = managerAccountId, At = now, NavUsd = navUsd, GrossUsd = grossUsd, NetUsd = netUsd });
        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The fund over the last hours on an even grid: ten seconds for up to an hour, five minutes beyond. At each step every manager counts with its latest sample
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
        // Each manager's last sample before the window opens it, however old: a manager that has not been read for a
        // while is still part of the fund at its last reading, as it is in the daily figures.
        var before = await database
            .NavSamples.AsNoTracking()
            .Where(s => s.ManagerAccount.Sleeve.FundId == fundId && s.At < from)
            .GroupBy(s => s.ManagerAccountId)
            .Select(g => new { ManagerAccountId = g.Key, At = g.Max(s => s.At) })
            .ToListAsync(cancellationToken);
        var samples = (
            await database
                .NavSamples.AsNoTracking()
                .Where(s => s.ManagerAccount.Sleeve.FundId == fundId && s.At >= from)
                .OrderBy(s => s.At)
                .Select(s => new { s.ManagerAccountId, s.At, s.NavUsd, s.GrossUsd, s.NetUsd })
                .ToListAsync(cancellationToken)
        );
        foreach (var seed in before)
        {
            samples.Insert(
                0,
                await database.NavSamples.AsNoTracking().Where(s => s.ManagerAccountId == seed.ManagerAccountId && s.At == seed.At).Select(s => new { s.ManagerAccountId, s.At, s.NavUsd, s.GrossUsd, s.NetUsd }).FirstAsync(cancellationToken)
            );
        }
        if (samples.Count == 0)
        {
            return new IntradayDto([], [], [], []);
        }

        TimeSpan step = hours <= FineHours ? FineStep : Step;
        long stepTicks = step.Ticks;
        DateTimeOffset first = samples.Any(s => s.At < from) ? from : samples[0].At;
        DateTimeOffset cursor = new(first.UtcTicks / stepTicks * stepTicks + stepTicks, TimeSpan.Zero);
        List<DateTimeOffset> times = [];
        List<decimal> nav = [],
            gross = [],
            net = [];
        Dictionary<int, (decimal Nav, decimal Gross, decimal Net)> latest = [];
        int next = 0;
        for (; cursor <= now + step; cursor += step)
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

    /// <summary>
    /// Removes samples older than ninety days, and thins those older than a day to the first of each five minutes for
    /// each manager. Ten-second samples are only worth keeping while someone may look at the last hour closely.
    /// </summary>
    /// <returns>How many samples were removed.</returns>
    public async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset cutoff = now - Retention;
        DateTimeOffset fineFrom = now - FineKept;
        List<long> remove = await database.NavSamples.Where(s => s.At < cutoff).Select(s => s.Id).ToListAsync(cancellationToken);

        // Run daily, so only the few days before the last one can still hold ten-second samples.
        DateTimeOffset thinFrom = fineFrom - TimeSpan.FromDays(7);
        var older = await database
            .NavSamples.AsNoTracking()
            .Where(s => s.At >= thinFrom && s.At < fineFrom && s.At >= cutoff)
            .OrderBy(s => s.ManagerAccountId)
            .ThenBy(s => s.At)
            .Select(s => new { s.Id, s.ManagerAccountId, s.At })
            .ToListAsync(cancellationToken);
        (int Manager, long Bucket) last = (0, -1);
        foreach (var sample in older)
        {
            (int, long) bucket = (sample.ManagerAccountId, sample.At.UtcTicks / Step.Ticks);
            if (bucket == last)
            {
                remove.Add(sample.Id);
            }

            last = bucket;
        }

        // In parts, so that a first run over several days of samples does not become one enormous command.
        foreach (long[] part in remove.Chunk(5000))
        {
            database.NavSamples.RemoveRange(part.Select(id => new NavSample { Id = id }));
            await database.SaveChangesAsync(cancellationToken);
        }

        return remove.Count;
    }
}
