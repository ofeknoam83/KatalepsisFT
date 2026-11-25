using System.Text.Json;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Risk;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Instruments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KatalepsisFT.Application.Execution;

// ---------------------------------------------------------------- what travels to and from the browser

/// <param name="TradingAccountId">The one account to work it on; or leave it out and give <paramref name="ManagerId"/>, and each slice goes to the manager's account with the best price.</param>
/// <param name="DurationMinutes">Over time: how long the order is spread over.</param>
/// <param name="Slices">Over time: how many slices it is cut into.</param>
/// <param name="MinSlice">Paced: the smallest slice, in units of the asset.</param>
/// <param name="MaxSlice">Paced: the largest slice.</param>
/// <param name="WaitSeconds">Paced: the wait between slices.</param>
/// <param name="PausePrice">Buying waits while the market is above this; selling while it is below.</param>
/// <param name="MaxSlippageBps">The order pauses itself when its average price is this much worse than the price it started at.</param>
public sealed record WorkingOrderRequest(
    int? TradingAccountId,
    int? ManagerId,
    string Asset,
    InstrumentType InstrumentType,
    OrderSide Side,
    decimal Quantity,
    decimal? LimitPrice,
    bool ReduceOnly,
    WorkingStyle Style,
    int? DurationMinutes,
    int? Slices,
    decimal? MinSlice,
    decimal? MaxSlice,
    int? WaitSeconds,
    decimal? PausePrice,
    int? MaxSlippageBps,
    string? ClientId
);

public sealed record SliceDto(long Id, DateTimeOffset At, Venue Venue, string VenueName, OrderType Type, decimal Quantity, decimal? LimitPrice, decimal FilledQuantity, decimal AverageFillPrice, decimal FeesUsd, OrderStatus Status, string? RejectReason);

/// <param name="SlippageBps">How much worse (positive) or better (negative) than the price at the start the fills have been, in hundredths of a percent.</param>
/// <param name="PlannedEndAt">Over time: when the last slice is due if nothing holds it up.</param>
/// <param name="Slices">Its slices, the latest first; left out of lists.</param>
public sealed record WorkingOrderDto(
    long Id,
    int ManagerId,
    string Manager,
    int? TradingAccountId,
    string Where,
    string Asset,
    InstrumentType InstrumentType,
    OrderSide Side,
    decimal Quantity,
    decimal? LimitPrice,
    bool ReduceOnly,
    WorkingStyle Style,
    int? DurationSeconds,
    int? SliceCount,
    decimal? MinSlice,
    decimal? MaxSlice,
    int? WaitSeconds,
    decimal? PausePrice,
    int? MaxSlippageBps,
    WorkingState State,
    string? Note,
    bool Stopping,
    decimal FilledQuantity,
    decimal AverageFillPrice,
    decimal? ArrivalPrice,
    decimal? SlippageBps,
    decimal FeesUsd,
    int SlicesSent,
    DateTimeOffset? StartedAt,
    DateTimeOffset? PlannedEndAt,
    DateTimeOffset? HardEndAt,
    DateTimeOffset? EndedAt,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? ApprovedBy,
    bool IsPaper,
    IReadOnlyList<SliceDto>? Slices
);

// ---------------------------------------------------------------- taking, steering and showing working orders

/// <summary>
/// Takes working orders from the ticket and lets a person pause, resume or stop them. Nothing here trades: a working
/// order is written down, and the execution service works it, slice by slice.
/// </summary>
public sealed class WorkingOrderService(IFtDbContext database, IBookFeed books, IExecutionSignal signal, TimeProvider clock)
{
    private static readonly TimeSpan LongestOpenEnded = TimeSpan.FromHours(24);

    public async Task<TradingChange<WorkingOrderDto>> SubmitAsync(WorkingOrderRequest request, Actor actor, CancellationToken cancellationToken)
    {
        string asset = (request.Asset ?? string.Empty).Trim().ToUpperInvariant();
        if (asset.Length is 0 or > 32 || !asset.All(char.IsLetterOrDigit))
        {
            return TradingChange<WorkingOrderDto>.Refused("Name the asset, for example BTC.");
        }

        if (request.InstrumentType is not (InstrumentType.Perp or InstrumentType.Spot) || !Enum.IsDefined(request.Side) || !Enum.IsDefined(request.Style))
        {
            return TradingChange<WorkingOrderDto>.Refused("Working orders are for perpetuals or spot, to buy or to sell, in one of the three ways offered.");
        }

        if (request.Quantity <= 0m || request.Quantity > 1_000_000_000m || request.LimitPrice is <= 0m or > 1_000_000_000m || request.PausePrice is <= 0m or > 1_000_000_000m)
        {
            return TradingChange<WorkingOrderDto>.Refused("The quantity and the prices must be above zero and within reason.");
        }

        if (request.ReduceOnly && request.InstrumentType != InstrumentType.Perp)
        {
            return TradingChange<WorkingOrderDto>.Refused("Reduce only is for perpetuals.");
        }

        if (request.MaxSlippageBps is < 1 or > 5000)
        {
            return TradingChange<WorkingOrderDto>.Refused("The largest slippage is between 1 and 5,000 hundredths of a percent.");
        }

        string? shape = request.Style switch
        {
            WorkingStyle.OverTime when request.DurationMinutes is not (>= 1 and <= 1440) => "Say how long to spread the order over: from one minute to a day.",
            WorkingStyle.OverTime when request.Slices is not (>= 2 and <= 500) => "Cut the order into between 2 and 500 slices.",
            WorkingStyle.OverTime when request.DurationMinutes!.Value * 60 / request.Slices!.Value < 5 => "That is a slice more often than every five seconds. Use fewer slices or a longer time.",
            WorkingStyle.Paced when request.MinSlice is not > 0m || request.MaxSlice is not > 0m || request.MaxSlice < request.MinSlice => "Give the smallest and the largest slice, the largest not below the smallest.",
            WorkingStyle.Paced when request.MaxSlice > request.Quantity => "The largest slice is more than the whole order.",
            WorkingStyle.Paced when request.WaitSeconds is not (>= 1 and <= 3600) => "The wait between slices is between a second and an hour.",
            WorkingStyle.Paced when request.Quantity / request.MinSlice!.Value > WorkingOrderEngine.MostSlices => $"At its smallest slice this order would take more than {WorkingOrderEngine.MostSlices} slices. Use larger slices.",
            _ => null,
        };
        if (shape is not null)
        {
            return TradingChange<WorkingOrderDto>.Refused(shape);
        }

        string clientId = string.IsNullOrWhiteSpace(request.ClientId) ? Guid.NewGuid().ToString("N") : request.ClientId.Trim();
        if (clientId.Length > 64 || !clientId.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            return TradingChange<WorkingOrderDto>.Refused("The order's own name may be up to 64 letters, digits, dashes, dots and underscores.");
        }

        IQueryable<TradingAccount> all = database.TradingAccounts.Include(a => a.ManagerAccount).ThenInclude(m => m.Sleeve).ThenInclude(s => s.Fund);
        List<TradingAccount> accounts = request.TradingAccountId is int accountId
            ? await all.Where(a => a.Id == accountId).ToListAsync(cancellationToken)
            : request.ManagerId is int managerId ? await all.Where(a => a.ManagerAccountId == managerId).OrderBy(a => a.Id).ToListAsync(cancellationToken) : [];
        if (accounts.Count == 0)
        {
            return request.TradingAccountId is null && request.ManagerId is null ? TradingChange<WorkingOrderDto>.Refused("Say which trading account, or which manager.") : TradingChange<WorkingOrderDto>.Missing;
        }

        Domain.Funds.ManagerAccount manager = accounts[0].ManagerAccount;
        if (manager.Sleeve.Fund.IsDemo)
        {
            return TradingChange<WorkingOrderDto>.Refused(TradingService.DemoIsFixed);
        }

        if (!manager.IsActive)
        {
            return TradingChange<WorkingOrderDto>.Refused(ConnectionService.RetiredManager);
        }

        WorkingOrder? existing = await database.WorkingOrders.AsNoTracking().FirstOrDefaultAsync(w => w.ManagerAccountId == manager.Id && w.ClientId == clientId, cancellationToken);
        if (existing is not null)
        {
            return TradingChange<WorkingOrderDto>.Ok(await DtoAsync(existing.Id, false, cancellationToken), "A working order with this name already exists; it is returned unchanged.");
        }

        // Its size in dollars decides whether a second person is needed, so there has to be a price to size it with.
        decimal? mid = ConsolidatedBook.Mid((await books.ReadAsync(asset, request.InstrumentType, cancellationToken)).ToList());
        if (mid is not decimal price)
        {
            return TradingChange<WorkingOrderDto>.Refused($"There is no live market for {asset} at the moment, so the order cannot be sized. Open its book and try again in a few seconds.");
        }

        decimal notional = request.Quantity * price;
        // Real money above what an account may send in one order takes a second person. Paper needs nobody's leave.
        TradingAccount? strictest = accounts.Where(a => a.Mode == TradingMode.Live).OrderBy(a => a.MaxOrderNotionalUsd).FirstOrDefault();
        bool needsApproval = strictest is not null && notional > strictest.MaxOrderNotionalUsd;
        DateTimeOffset now = clock.GetUtcNow();
        WorkingOrder working = new()
        {
            ClientId = clientId,
            ManagerAccountId = manager.Id,
            TradingAccountId = request.TradingAccountId,
            Asset = asset,
            InstrumentType = request.InstrumentType,
            Side = request.Side,
            Quantity = request.Quantity,
            LimitPrice = request.LimitPrice,
            ReduceOnly = request.ReduceOnly,
            Style = request.Style,
            DurationSeconds = request.Style == WorkingStyle.OverTime ? request.DurationMinutes * 60 : null,
            Slices = request.Style == WorkingStyle.OverTime ? request.Slices : null,
            MinSlice = request.Style == WorkingStyle.Paced ? request.MinSlice : null,
            MaxSlice = request.Style == WorkingStyle.Paced ? request.MaxSlice : null,
            WaitSeconds = request.Style == WorkingStyle.Paced ? request.WaitSeconds : null,
            PausePrice = request.PausePrice,
            MaxSlippageBps = request.MaxSlippageBps,
            State = needsApproval ? WorkingState.PendingApproval : WorkingState.Working,
            CreatedBy = actor.Name,
            CreatedAt = now,
        };
        if (!needsApproval)
        {
            Start(working, now);
        }

        database.WorkingOrders.Add(working);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The same name arrived twice at the same instant; the other one won.
            database.DiscardChanges();
            WorkingOrder? won = await database.WorkingOrders.AsNoTracking().FirstOrDefaultAsync(w => w.ManagerAccountId == manager.Id && w.ClientId == clientId, cancellationToken);
            return won is null ? TradingChange<WorkingOrderDto>.Refused("The order could not be recorded. Try again.") : TradingChange<WorkingOrderDto>.Ok(await DtoAsync(won.Id, false, cancellationToken));
        }

        string? notice = null;
        if (needsApproval)
        {
            string where = request.TradingAccountId is null ? $"{manager.Name}, best venue" : $"{manager.Name} · {VenueExposureService.NameOf(accounts[0].Venue)}";
            database.ApprovalRequests.Add(
                new ApprovalRequest
                {
                    Kind = ApprovalKind.LargeOrder,
                    FundId = manager.Sleeve.FundId,
                    TradingAccountId = request.TradingAccountId,
                    Summary = $"Work an order to {(request.Side == OrderSide.Buy ? "buy" : "sell")} {request.Quantity:0.########} {(request.InstrumentType == InstrumentType.Spot ? asset + " spot" : asset + "-PERP")} on {where}: about {notional:N0} USD, above the {strictest!.MaxOrderNotionalUsd:N0} USD one order may be. {Describe(working)}.",
                    PayloadJson = JsonSerializer.Serialize(new { workingOrderId = working.Id }),
                    RequestedBy = actor.Name,
                    RequestedAt = now,
                }
            );
            await database.SaveChangesAsync(cancellationToken);
            notice = "Recorded. It starts when a second administrator approves it.";
        }
        else
        {
            await signal.WakeAsync(cancellationToken);
        }

        return TradingChange<WorkingOrderDto>.Ok(await DtoAsync(working.Id, false, cancellationToken), notice);
    }

    /// <summary>What a decision on a large order does to it. Called with the decision, before it is saved.</summary>
    public static async Task<bool> DecideAsync(IFtDbContext database, ApprovalRequest request, bool approve, string by, DateTimeOffset now, CancellationToken cancellationToken)
    {
        long id;
        try
        {
            id = JsonDocument.Parse(request.PayloadJson).RootElement.GetProperty("workingOrderId").GetInt64();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }

        WorkingOrder? working = await database.WorkingOrders.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (working is null || working.State != WorkingState.PendingApproval)
        {
            return false;
        }

        if (approve)
        {
            working.State = WorkingState.Working;
            working.ApprovedBy = by;
            Start(working, now);
        }
        else
        {
            working.State = WorkingState.Rejected;
            working.EndedAt = now;
            working.Note = $"Not approved by {by}.";
        }

        return true;
    }

    public async Task<TradingChange<WorkingOrderDto>> PauseAsync(long id, Actor actor, CancellationToken cancellationToken) =>
        await ChangeAsync(id, w => w.State == WorkingState.Working ? null : "Only an order that is working can be paused.", w =>
        {
            w.State = WorkingState.Paused;
            w.Note = $"Paused by {actor.Name}.";
        }, cancellationToken);

    public async Task<TradingChange<WorkingOrderDto>> ResumeAsync(long id, Actor actor, CancellationToken cancellationToken) =>
        await ChangeAsync(id, w => w.State == WorkingState.Paused ? null : "Only a paused order can be resumed.", w =>
        {
            // An order its own slippage guard paused goes on without that guard: a person has looked and said carry on.
            w.SlippageAccepted |= w.Note?.StartsWith(WorkingOrderEngine.SlippageNote, StringComparison.Ordinal) == true;
            w.State = WorkingState.Working;
            w.Note = $"Resumed by {actor.Name}.";
            w.NextSliceAt = clock.GetUtcNow();
        }, cancellationToken);

    public async Task<TradingChange<WorkingOrderDto>> StopAsync(long id, Actor actor, CancellationToken cancellationToken)
    {
        TradingChange<WorkingOrderDto> change = await ChangeAsync(id, w => w.IsOpen ? null : "The order has already ended.", w =>
        {
            if (w.State == WorkingState.PendingApproval)
            {
                w.State = WorkingState.Stopped;
                w.EndedAt = clock.GetUtcNow();
                w.Note = $"Withdrawn by {actor.Name} before it was approved.";
            }
            else
            {
                w.StopRequested = true;
                w.Note = $"Stop asked for by {actor.Name}.";
            }
        }, cancellationToken);
        if (change.Done && change.Value!.State == WorkingState.Stopped)
        {
            // What was waiting for a second person no longer needs one.
            foreach (ApprovalRequest waiting in await database.ApprovalRequests.Where(r => r.Kind == ApprovalKind.LargeOrder && r.Status == ApprovalStatus.Pending).ToListAsync(cancellationToken))
            {
                if (waiting.PayloadJson.Contains($"\"workingOrderId\":{id}}}", StringComparison.Ordinal))
                {
                    waiting.Status = ApprovalStatus.Cancelled;
                    waiting.DecidedAt = clock.GetUtcNow();
                    waiting.DecidedBy = actor.Name;
                }
            }

            await database.SaveChangesAsync(cancellationToken);
        }

        return change;
    }

    private async Task<TradingChange<WorkingOrderDto>> ChangeAsync(long id, Func<WorkingOrder, string?> refusal, Action<WorkingOrder> change, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            WorkingOrder? working = await database.WorkingOrders.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
            if (working is null)
            {
                return TradingChange<WorkingOrderDto>.Missing;
            }

            if (refusal(working) is { } why)
            {
                return TradingChange<WorkingOrderDto>.Refused(why);
            }

            change(working);
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                await signal.WakeAsync(cancellationToken);
                return TradingChange<WorkingOrderDto>.Ok(await DtoAsync(id, false, cancellationToken));
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                // The engine wrote to it at the same moment; the change is made again on what it left.
                database.DiscardChanges();
            }
        }
    }

    /// <summary>A fund's working orders: the open ones, and those that ended in the last few days.</summary>
    public async Task<IReadOnlyList<WorkingOrderDto>> ListAsync(int fundId, CancellationToken cancellationToken)
    {
        DateTimeOffset since = clock.GetUtcNow() - TimeSpan.FromDays(7);
        List<long> ids = await database
            .WorkingOrders.Where(w => w.ManagerAccount.Sleeve.FundId == fundId && (w.EndedAt == null || w.EndedAt >= since))
            .OrderByDescending(w => w.Id)
            .Take(50)
            .Select(w => w.Id)
            .ToListAsync(cancellationToken);
        List<WorkingOrderDto> result = [];
        foreach (long id in ids)
        {
            result.Add((await DtoAsync(id, false, cancellationToken))!);
        }

        return result;
    }

    public async Task<WorkingOrderDto?> GetAsync(long id, CancellationToken cancellationToken) => await DtoAsync(id, true, cancellationToken);

    private async Task<WorkingOrderDto?> DtoAsync(long id, bool withSlices, CancellationToken cancellationToken)
    {
        WorkingOrder? w = await database.WorkingOrders.AsNoTracking().Include(x => x.ManagerAccount).Include(x => x.TradingAccount).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (w is null)
        {
            return null;
        }

        List<SliceDto>? slices = null;
        if (withSlices)
        {
            var rows = await database.Orders.AsNoTracking().Where(o => o.WorkingOrderId == id).OrderByDescending(o => o.Id).Take(WorkingOrderEngine.MostSlices).Select(o => new { Order = o, o.TradingAccount.Venue }).ToListAsync(cancellationToken);
            slices = rows.Select(r => new SliceDto(r.Order.Id, r.Order.CreatedAt, r.Venue, VenueExposureService.NameOf(r.Venue), r.Order.Type, r.Order.Quantity, r.Order.LimitPrice, r.Order.FilledQuantity, r.Order.AverageFillPrice, r.Order.FeesUsd, r.Order.Status, r.Order.RejectReason)).ToList();
        }

        bool paper = w.TradingAccount is not null ? w.TradingAccount.Mode == TradingMode.Paper : !await database.TradingAccounts.AnyAsync(a => a.ManagerAccountId == w.ManagerAccountId && a.Mode == TradingMode.Live, cancellationToken);
        decimal? slippage = w.ArrivalPrice is decimal arrival && arrival > 0m && w.FilledQuantity > 0m ? Math.Round((w.Side == OrderSide.Buy ? 1m : -1m) * (w.AverageFillPrice - arrival) / arrival * 10_000m, 2) : null;
        return new WorkingOrderDto(
            w.Id,
            w.ManagerAccountId,
            w.ManagerAccount.Name,
            w.TradingAccountId,
            w.TradingAccount is null ? "Best venue for each slice" : VenueExposureService.NameOf(w.TradingAccount.Venue),
            w.Asset,
            w.InstrumentType,
            w.Side,
            w.Quantity,
            w.LimitPrice,
            w.ReduceOnly,
            w.Style,
            w.DurationSeconds,
            w.Slices,
            w.MinSlice,
            w.MaxSlice,
            w.WaitSeconds,
            w.PausePrice,
            w.MaxSlippageBps,
            w.State,
            w.Note,
            w.StopRequested && w.IsOpen,
            w.FilledQuantity,
            w.AverageFillPrice,
            w.ArrivalPrice,
            slippage,
            w.FeesUsd,
            w.SlicesSent,
            w.StartedAt,
            w.Style == WorkingStyle.OverTime && w.StartedAt is DateTimeOffset started ? started + TimeSpan.FromSeconds(w.DurationSeconds ?? 0) : null,
            w.HardEndAt,
            w.EndedAt,
            w.CreatedBy,
            w.CreatedAt,
            w.ApprovedBy,
            paper,
            slices
        );
    }

    /// <summary>The clock starts: now, not when the order was written down, so waiting for approval costs it no time.</summary>
    private static void Start(WorkingOrder working, DateTimeOffset now)
    {
        working.StartedAt = now;
        working.NextSliceAt = now;
        // Every working order has an end it cannot run past. Spread over a set time, it gets half that time again
        // and ten minutes to make up for pauses; the open-ended kinds get a day.
        working.HardEndAt = now + (working.Style == WorkingStyle.OverTime ? TimeSpan.FromSeconds(working.DurationSeconds!.Value * 1.5) + TimeSpan.FromMinutes(10) : LongestOpenEnded);
    }

    private static string Describe(WorkingOrder w) =>
        w.Style switch
        {
            WorkingStyle.OverTime => $"Spread over {TimeSpan.FromSeconds(w.DurationSeconds ?? 0).TotalMinutes:0} minutes in {w.Slices} slices",
            WorkingStyle.Paced => $"In slices of {w.MinSlice:0.########} to {w.MaxSlice:0.########}, {w.WaitSeconds} seconds apart",
            _ => "Resting at the best price as a maker",
        };
}

// ---------------------------------------------------------------- working them

/// <summary>
/// Works the working orders, in the execution service, one look at each every round. A look reads the order's slices
/// back from the order log, so what has been filled is never a guess, and then either waits, sends the next slice,
/// or ends the order. A slice is an ordinary order: it is admitted, checked against the account's limits and filled
/// by the same engine as an order from the ticket, on paper or for real.
///
/// The slicing follows what Katalepsis does in its fix-balance engine (slices of a random size with a wait between
/// them, a price beyond which it waits, resting as a maker) and in its RIA module (even slices across a set time).
/// Sending each slice to the venue with the best price is new here.
/// </summary>
public sealed class WorkingOrderEngine(IFtDbContext database, ConsolidatedBook books, TimeProvider clock, ILogger<WorkingOrderEngine> logger)
{
    /// <summary>No working order sends more slices than this, whatever else is true of it.</summary>
    public const int MostSlices = 1000;

    /// <summary>This many slices refused one after another end the order: something is wrong that more slices will not fix.</summary>
    public const int MostRefusalsInARow = 3;

    public const string SlippageNote = "Paused by its slippage guard";

    /// <summary>A resting slice is left alone for this long before it is moved to a new best price.</summary>
    private static readonly TimeSpan RequoteAfter = TimeSpan.FromSeconds(2);

    private readonly Dictionary<(Venue, InstrumentType, string, string), decimal?> _steps = [];

    /// <returns>How many working orders changed.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        List<long> open = await database.WorkingOrders.Where(w => w.State == WorkingState.Working || w.State == WorkingState.Paused).OrderBy(w => w.Id).Select(w => w.Id).ToListAsync(cancellationToken);
        if (open.Count == 0)
        {
            return 0;
        }

        List<TradingHalt> halts = await database.TradingHalts.AsNoTracking().Where(h => h.ReleasedAt == null).ToListAsync(cancellationToken);
        int changed = 0;
        foreach (long id in open)
        {
            try
            {
                if (await HandleAsync(id, halts, cancellationToken))
                {
                    changed++;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One order that cannot be handled must not stop the others; it is looked at again on the next round.
                database.DiscardChanges();
                logger.LogError(e, "Working order {WorkingOrderId} could not be handled.", id);
            }
        }

        return changed;
    }

    private async Task<bool> HandleAsync(long id, List<TradingHalt> halts, CancellationToken cancellationToken)
    {
        database.DiscardChanges();
        WorkingOrder? w = await database.WorkingOrders.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (w is null || w.State is not (WorkingState.Working or WorkingState.Paused))
        {
            return false;
        }

        List<Order> slices = await database.Orders.Where(o => o.WorkingOrderId == id).OrderBy(o => o.Id).ToListAsync(cancellationToken);
        List<TradingAccount> accounts = await (w.TradingAccountId is int one ? database.TradingAccounts.Where(a => a.Id == one) : database.TradingAccounts.Where(a => a.ManagerAccountId == w.ManagerAccountId)).OrderBy(a => a.Id).ToListAsync(cancellationToken);
        DateTimeOffset now = clock.GetUtcNow();

        // What has been done is what the slices say, every time.
        decimal filled = slices.Sum(s => s.FilledQuantity);
        w.FilledQuantity = filled;
        w.AverageFillPrice = filled > 0m ? Math.Round(slices.Sum(s => s.FilledQuantity * s.AverageFillPrice) / filled, 10) : 0m;
        w.FeesUsd = slices.Sum(s => s.FeesUsd);
        List<Order> open = slices.Where(s => s.IsOpen).ToList();
        int refusals = slices.AsEnumerable().Reverse().TakeWhile(s => s.Status == OrderStatus.Rejected).Count();
        decimal remaining = w.Quantity - filled;
        List<TradingAccount> usable = accounts.Where(a => !Halted(a, halts)).ToList();

        // ---- reasons to end
        (WorkingState State, string Note)? end =
            w.StopRequested ? (WorkingState.Stopped, w.Note ?? "Stopped on request.")
            : usable.Count == 0 ? (WorkingState.Stopped, "Stopped by the kill switch.")
            : remaining <= w.Quantity * 0.0000001m ? (WorkingState.Done, "All of it has traded.")
            : w.HardEndAt is DateTimeOffset latest && now >= latest ? (WorkingState.Stopped, "Stopped: it reached the latest time it was allowed to run to.")
            : slices.Count >= MostSlices ? (WorkingState.Stopped, $"Stopped: it had sent {MostSlices} slices, the most any order may.")
            : refusals >= MostRefusalsInARow ? (WorkingState.Stopped, $"Stopped: {MostRefusalsInARow} slices in a row were refused. The last reason: {slices[^1].RejectReason}")
            : null;
        if (end is { } ending)
        {
            CancelOpen(open, now);
            if (open.Count == 0)
            {
                w.State = ending.State;
                w.EndedAt = now;
                w.Note = Short(ending.Note);
            }

            return await SaveAsync(cancellationToken);
        }

        if (w.State == WorkingState.Paused)
        {
            // Nothing rests in the market while the order is held.
            CancelOpen(open, now);
            return await SaveAsync(cancellationToken);
        }

        // ---- its own guards
        if (w.MaxSlippageBps is int most && !w.SlippageAccepted && w.ArrivalPrice is decimal arrival && arrival > 0m && filled > 0m)
        {
            decimal slippage = (w.Side == OrderSide.Buy ? 1m : -1m) * (w.AverageFillPrice - arrival) / arrival * 10_000m;
            if (slippage > most)
            {
                CancelOpen(open, now);
                w.State = WorkingState.Paused;
                w.Note = Short($"{SlippageNote}: the average price is {slippage:0.#} bp worse than the {arrival:0.########} it started at, and the limit is {most} bp. Resume to carry on regardless.");
                return await SaveAsync(cancellationToken);
            }
        }

        (TradingAccount Account, VenueBook Book)? best = Choose(w, usable);
        if (best is not { } chosen)
        {
            w.Note = "Waiting: no venue it can trade on has a live book for it right now.";
            return await SaveAsync(cancellationToken);
        }

        decimal touch = (w.Side == OrderSide.Buy ? chosen.Book.BestAsk : chosen.Book.BestBid)!.Value;
        w.ArrivalPrice ??= chosen.Book.Mid;
        if (w.PausePrice is decimal hold && (w.Side == OrderSide.Buy ? touch > hold : touch < hold))
        {
            CancelOpen(open, now);
            w.Note = Short($"Waiting: the market is at {touch:0.########}, {(w.Side == OrderSide.Buy ? "above" : "below")} the pause price of {hold:0.########}.");
            return await SaveAsync(cancellationToken);
        }

        if (w.Note is not null && (w.Note.StartsWith("Waiting", StringComparison.Ordinal) || w.Note.StartsWith("Resumed", StringComparison.Ordinal)))
        {
            w.Note = null;
        }

        // ---- the next slice
        if (w.Style == WorkingStyle.Passive)
        {
            decimal rest = (w.Side == OrderSide.Buy ? chosen.Book.BestBid : chosen.Book.BestAsk) ?? touch;
            if (w.LimitPrice is decimal cap)
            {
                rest = w.Side == OrderSide.Buy ? Math.Min(rest, cap) : Math.Max(rest, cap);
            }

            if (open.Count > 0)
            {
                // A resting slice that is no longer at the best price is taken back; the next look puts a new one there.
                foreach (Order resting in open.Where(o => !o.CancelRequested && o.LimitPrice != rest && now - o.CreatedAt >= RequoteAfter))
                {
                    resting.CancelRequested = true;
                    resting.UpdatedAt = now;
                }

                return await SaveAsync(cancellationToken);
            }

            await SendAsync(w, chosen.Account, remaining, rest, OrderType.Limit, TimeInForce.PostOnly, rest, now, cancellationToken);
            return await SaveAsync(cancellationToken);
        }

        // One slice at a time: the next is not sent while the last is still being filled.
        if (open.Count > 0 || (w.NextSliceAt is DateTimeOffset due && now < due))
        {
            return await SaveAsync(cancellationToken);
        }

        decimal wanted;
        if (w.Style == WorkingStyle.OverTime)
        {
            decimal each = w.Quantity / w.Slices!.Value;
            double interval = (double)w.DurationSeconds!.Value / w.Slices.Value;
            int dueSlices = Math.Min(w.Slices.Value, (int)Math.Floor((now - w.StartedAt!.Value).TotalSeconds / interval) + 1);
            // Behind its schedule after a pause, it makes the time up at no more than double speed.
            wanted = Math.Clamp(dueSlices * each - filled, each, each * 2m);
            w.NextSliceAt = now + TimeSpan.FromSeconds(interval);
        }
        else
        {
            wanted = w.MinSlice!.Value + (decimal)Random.Shared.NextDouble() * (w.MaxSlice!.Value - w.MinSlice.Value);
            w.NextSliceAt = now + TimeSpan.FromSeconds(w.WaitSeconds!.Value);
        }

        await SendAsync(w, chosen.Account, Math.Min(wanted, remaining), touch, w.LimitPrice is null ? OrderType.Market : OrderType.Limit, TimeInForce.ImmediateOrCancel, w.LimitPrice, now, cancellationToken);
        return await SaveAsync(cancellationToken);
    }

    /// <summary>Writes the next slice down as an order. The slice and the working order's count of slices are saved together.</summary>
    private async Task SendAsync(WorkingOrder w, TradingAccount account, decimal quantity, decimal price, OrderType type, TimeInForce timeInForce, decimal? limit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        decimal? step = await StepAsync(account, w, cancellationToken);
        decimal remaining = w.Quantity - w.FilledQuantity;
        // No slice is larger than one order may be on that account, with a little room for the price moving.
        decimal size = Math.Min(quantity, account.MaxOrderNotionalUsd / price * 0.98m);
        if (step is decimal unit && unit > 0m)
        {
            size = Math.Floor(size / unit) * unit;
            if (size <= 0m && remaining < unit)
            {
                w.State = WorkingState.Done;
                w.EndedAt = now;
                w.Note = Short($"Done: the {remaining:0.########} left is less than the smallest step {account.Venue} takes.");
                return;
            }
        }

        if (size <= 0m)
        {
            w.Note = "Waiting: the next slice comes out at nothing. The account's largest order may be too small for this instrument.";
            return;
        }

        w.SlicesSent++;
        Order slice = new()
        {
            ClientOrderId = $"w{w.Id}-{w.SlicesSent}",
            TradingAccountId = account.Id,
            Asset = w.Asset,
            InstrumentType = w.InstrumentType,
            Side = w.Side,
            Type = type,
            TimeInForce = timeInForce,
            Quantity = size,
            LimitPrice = type == OrderType.Market ? null : limit,
            ReduceOnly = w.ReduceOnly,
            Status = OrderStatus.Received,
            Source = OrderSource.Working,
            SubmittedBy = w.CreatedBy,
            WorkingOrderId = w.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        slice.Events.Add(new OrderEvent { At = now, Kind = OrderEventKind.Received, Quantity = size, Price = slice.LimitPrice, Detail = $"Slice {w.SlicesSent} of working order {w.Id}." });
        database.Orders.Add(slice);
    }

    /// <summary>
    /// The account to send the next slice to: the only one, or among the manager's accounts the one whose venue has
    /// the best price after its fee for taking. Only accounts with a live book count.
    /// </summary>
    private (TradingAccount Account, VenueBook Book)? Choose(WorkingOrder w, List<TradingAccount> accounts)
    {
        (TradingAccount Account, VenueBook Book, decimal Cost)? best = null;
        foreach (TradingAccount account in accounts)
        {
            VenueBook? book = books.Of(account.Venue, w.Asset, w.InstrumentType, account.QuoteFor(w.InstrumentType));
            if ((w.Side == OrderSide.Buy ? book?.BestAsk : book?.BestBid) is not decimal price)
            {
                continue;
            }

            decimal taker = VenueFees.Of(account.Venue, w.InstrumentType).Taker;
            // What a unit costs to buy, or the negative of what it brings in to sell: smaller is better either way.
            decimal cost = w.Side == OrderSide.Buy ? price * (1m + taker) : -price * (1m - taker);
            if (best is null || cost < best.Value.Cost)
            {
                best = (account, book!, cost);
            }
        }

        return best is { } found ? (found.Account, found.Book) : null;
    }

    private async Task<decimal?> StepAsync(TradingAccount account, WorkingOrder w, CancellationToken cancellationToken)
    {
        string quote = account.QuoteFor(w.InstrumentType) ?? string.Empty;
        if (_steps.TryGetValue((account.Venue, w.InstrumentType, w.Asset, quote), out decimal? known))
        {
            return known;
        }

        var listed = await database.ListedInstruments.AsNoTracking().Where(i => i.Venue == account.Venue && i.Type == w.InstrumentType && i.Asset == w.Asset).Select(i => new { i.Quote, i.QuantityStep }).ToListAsync(cancellationToken);
        decimal? step = (listed.FirstOrDefault(i => i.Quote == quote) ?? listed.FirstOrDefault(i => i.Quote == "USDT") ?? listed.FirstOrDefault())?.QuantityStep;
        return _steps[(account.Venue, w.InstrumentType, w.Asset, quote)] = step;
    }

    private static bool Halted(TradingAccount account, List<TradingHalt> halts) =>
        halts.Any(h => h.Scope == HaltScope.Global || (h.Scope == HaltScope.Venue && h.Venue == account.Venue) || (h.Scope == HaltScope.Account && h.TradingAccountId == account.Id));

    private static void CancelOpen(List<Order> open, DateTimeOffset now)
    {
        foreach (Order slice in open.Where(o => !o.CancelRequested))
        {
            slice.CancelRequested = true;
            slice.UpdatedAt = now;
        }
    }

    private async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        if (!database.HasChanges())
        {
            return false;
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // A person paused or stopped it, or a slice was filled, in the same instant. Nothing is lost: the next look starts from what is stored.
            database.DiscardChanges();
            return false;
        }
    }

    private static string Short(string note) => note.Length <= 400 ? note : note[..400];
}
