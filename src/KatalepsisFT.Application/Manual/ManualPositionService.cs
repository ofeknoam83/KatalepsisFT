using System.Globalization;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Prices;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Domain.Positions;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Manual;

/// <param name="Price">USD per unit; empty to use the asset's latest daily close. For an option, the premium.</param>
public sealed record ManualPositionRequest(
    InstrumentType Type,
    string Asset,
    decimal Quantity,
    decimal? Price,
    decimal? EntryPrice,
    DateOnly? Expiry,
    decimal? Strike,
    OptionRight? OptionRight,
    decimal? Delta,
    decimal? UnderlyingPrice,
    string? Note
);

/// <param name="MarkPrice">The price the position is valued at now.</param>
/// <param name="PriceFromFeed">True when that price is the asset's latest daily close rather than one entered by hand.</param>
public sealed record ManualPositionDto(
    int Id,
    InstrumentType Type,
    string Asset,
    decimal Quantity,
    decimal? Price,
    decimal? EntryPrice,
    DateOnly? Expiry,
    decimal? Strike,
    OptionRight? OptionRight,
    decimal? Delta,
    decimal? UnderlyingPrice,
    string? Note,
    decimal? MarkPrice,
    bool PriceFromFeed,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy
);

/// <param name="FromVenue">Read from a venue; such a flow cannot be removed by hand.</param>
public sealed record CashFlowDto(long Id, DateOnly Date, decimal AmountUsd, string? Note, bool FromVenue);

/// <param name="AmountUsd">Positive is money in, negative money out.</param>
public sealed record CashFlowRequest(DateOnly Date, decimal AmountUsd, string? Note);

/// <param name="CanEdit">False for the built-in example fund.</param>
public sealed record ManualBookDto(int ManagerId, string Manager, bool CanEdit, IReadOnlyList<ManualPositionDto> Positions, IReadOnlyList<CashFlowDto> CashFlows);

/// <summary>Outcome of a change: done, refused with reasons (one per faulty line for an upload), or not found.</summary>
public sealed record ManualChange(bool Done, IReadOnlyList<string> Errors, bool NotFound = false)
{
    public static ManualChange Ok { get; } = new(true, []);

    public static ManualChange Missing { get; } = new(false, [], true);

    public static ManualChange Refused(params string[] errors) => new(false, errors);
}

/// <summary>Hand-entered positions and cash flows of a manager: entry, upload of a whole book, and what follows for the fund's figures.</summary>
public sealed class ManualPositionService(IFtDbContext database, ManagerBookService books, PriceBackfillService prices, TimeProvider clock)
{
    public const int MaxUploadLines = 2_000;
    /// <summary>History loaded for an asset first seen through a manual position.</summary>
    private const int HistoryDays = 400;

    /// <summary>How many unseen assets one entry or upload may trigger a price lookup for.</summary>
    private const int MaxHistoryLookups = 40;
    public const string CsvHeader = "type,asset,quantity,price,entry_price,expiry,strike,right,delta,underlying_price,note";
    private const string DemoIsFixed = "The example fund is rebuilt from its definition and cannot be edited.";

    public async Task<ManualBookDto?> GetAsync(int managerId, CancellationToken cancellationToken)
    {
        var manager = await database.ManagerAccounts.Where(m => m.Id == managerId).Select(m => new { m.Name, m.Sleeve.Fund.IsDemo }).FirstOrDefaultAsync(cancellationToken);
        if (manager is null)
        {
            return null;
        }

        List<ManualPosition> positions = await database.ManualPositions.AsNoTracking().Where(p => p.ManagerAccountId == managerId).OrderBy(p => p.Type).ThenBy(p => p.Asset).ThenBy(p => p.Id).ToListAsync(cancellationToken);
        Dictionary<string, decimal> closes = await ManagerBookWriter.LatestClosesAsync(database, positions.Select(p => p.Asset).Distinct().ToArray(), cancellationToken);
        List<CashFlow> flows = await database.CashFlows.AsNoTracking().Where(f => f.ManagerAccountId == managerId).OrderByDescending(f => f.Date).ThenByDescending(f => f.Id).Take(200).ToListAsync(cancellationToken);
        return new ManualBookDto(
            managerId,
            manager.Name,
            !manager.IsDemo,
            positions
                .Select(p =>
                {
                    decimal? feed = closes.TryGetValue(p.Asset, out decimal close) ? close : null;
                    bool fromFeed = p.Price is null && p.Type is not (InstrumentType.Cash or InstrumentType.Option) && feed is not null;
                    return new ManualPositionDto(p.Id, p.Type, p.Asset, p.Quantity, p.Price, p.EntryPrice, p.Expiry, p.Strike, p.OptionRight, p.Delta, p.UnderlyingPrice, p.Note, p.Price ?? (p.Type == InstrumentType.Cash ? 1m : fromFeed ? feed : p.EntryPrice), fromFeed, p.UpdatedAt, p.UpdatedBy);
                })
                .ToList(),
            flows.Select(f => new CashFlowDto(f.Id, f.Date, f.AmountUsd, f.Note, f.ExternalId is not null)).ToList()
        );
    }

    public async Task<ManualChange> AddAsync(int managerId, ManualPositionRequest request, string user, CancellationToken cancellationToken)
    {
        if (await RefusalAsync(managerId, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        List<string> errors = await ValidateAsync([request], null, cancellationToken);
        if (errors.Count > 0)
        {
            return new ManualChange(false, errors);
        }

        database.ManualPositions.Add(Apply(new ManualPosition { ManagerAccountId = managerId, Asset = string.Empty }, request, user));
        await database.SaveChangesAsync(cancellationToken);
        await books.RefreshAsync(managerId, cancellationToken);
        return ManualChange.Ok;
    }

    public async Task<ManualChange> UpdateAsync(int positionId, ManualPositionRequest request, string user, CancellationToken cancellationToken)
    {
        ManualPosition? position = await database.ManualPositions.FirstOrDefaultAsync(p => p.Id == positionId, cancellationToken);
        if (position is null)
        {
            return ManualChange.Missing;
        }

        if (await RefusalAsync(position.ManagerAccountId, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        List<string> errors = await ValidateAsync([request], null, cancellationToken);
        if (errors.Count > 0)
        {
            return new ManualChange(false, errors);
        }

        Apply(position, request, user);
        await database.SaveChangesAsync(cancellationToken);
        await books.RefreshAsync(position.ManagerAccountId, cancellationToken);
        return ManualChange.Ok;
    }

    public async Task<ManualChange> DeleteAsync(int positionId, CancellationToken cancellationToken)
    {
        ManualPosition? position = await database.ManualPositions.FirstOrDefaultAsync(p => p.Id == positionId, cancellationToken);
        if (position is null)
        {
            return ManualChange.Missing;
        }

        if (await RefusalAsync(position.ManagerAccountId, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        database.ManualPositions.Remove(position);
        await database.SaveChangesAsync(cancellationToken);
        await books.RefreshAsync(position.ManagerAccountId, cancellationToken);
        return ManualChange.Ok;
    }

    /// <summary>
    /// Replaces the manager's whole manual book with the lines of a CSV file. Every line is checked first; one faulty
    /// line refuses the file and leaves the book as it was, so a half-loaded book can never be valued.
    /// </summary>
    public async Task<ManualChange> UploadAsync(int managerId, string csv, string user, CancellationToken cancellationToken)
    {
        if (await RefusalAsync(managerId, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        (List<ManualPositionRequest> rows, List<int> lineNumbers, List<string> errors) = ManualCsv.Parse(csv);
        if (errors.Count == 0)
        {
            errors.AddRange(await ValidateAsync(rows, lineNumbers, cancellationToken));
        }

        if (errors.Count > 0)
        {
            return new ManualChange(false, errors.Take(50).ToList());
        }

        database.ManualPositions.RemoveRange(await database.ManualPositions.Where(p => p.ManagerAccountId == managerId).ToListAsync(cancellationToken));
        database.ManualPositions.AddRange(rows.Select(r => Apply(new ManualPosition { ManagerAccountId = managerId, Asset = string.Empty }, r, user)));
        await database.SaveChangesAsync(cancellationToken);
        await books.RefreshAsync(managerId, cancellationToken);
        return ManualChange.Ok;
    }

    public async Task<ManualChange> AddCashFlowAsync(int managerId, CashFlowRequest request, CancellationToken cancellationToken)
    {
        if (await RefusalAsync(managerId, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        DateOnly today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (request.AmountUsd == 0m || Math.Abs(request.AmountUsd) > 1_000_000_000_000m)
        {
            return ManualChange.Refused("Give an amount other than zero: positive for money in, negative for money out.");
        }

        if (request.Date > today || request.Date < today.AddYears(-20))
        {
            return ManualChange.Refused("The date must be today or earlier.");
        }

        database.CashFlows.Add(
            new CashFlow
            {
                ManagerAccountId = managerId,
                Date = request.Date,
                AmountUsd = Math.Round(request.AmountUsd, 2),
                Kind = request.AmountUsd > 0m ? CashFlowKind.Subscription : CashFlowKind.Redemption,
                Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()[..Math.Min(request.Note.Trim().Length, 500)],
            }
        );
        await database.SaveChangesAsync(cancellationToken);
        return ManualChange.Ok;
    }

    public async Task<ManualChange> DeleteCashFlowAsync(long flowId, CancellationToken cancellationToken)
    {
        CashFlow? flow = await database.CashFlows.FirstOrDefaultAsync(f => f.Id == flowId, cancellationToken);
        if (flow is null)
        {
            return ManualChange.Missing;
        }

        if (await RefusalAsync(flow.ManagerAccountId, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        if (flow.ExternalId is not null)
        {
            return ManualChange.Refused("This flow was read from a venue and cannot be removed by hand.");
        }

        database.CashFlows.Remove(flow);
        await database.SaveChangesAsync(cancellationToken);
        return ManualChange.Ok;
    }

    private async Task<ManualChange?> RefusalAsync(int managerId, CancellationToken cancellationToken)
    {
        var manager = await database.ManagerAccounts.Where(m => m.Id == managerId).Select(m => new { m.Sleeve.Fund.IsDemo, m.IsActive }).FirstOrDefaultAsync(cancellationToken);
        return manager switch
        {
            null => ManualChange.Missing,
            { IsDemo: true } => ManualChange.Refused(DemoIsFixed),
            { IsActive: false } => ManualChange.Refused(Connections.ConnectionService.RetiredManager),
            _ => null,
        };
    }

    /// <summary>The rules a position must meet to be valued; each message names the line when lines are numbered.</summary>
    private async Task<List<string>> ValidateAsync(IReadOnlyList<ManualPositionRequest> rows, IReadOnlyList<int>? lineNumbers, CancellationToken cancellationToken)
    {
        await LoadMissingHistoryAsync(rows, cancellationToken);
        Dictionary<string, decimal> closes = await ManagerBookWriter.LatestClosesAsync(database, rows.Select(r => (r.Asset ?? string.Empty).Trim().ToUpperInvariant()).Where(a => a.Length > 0).Distinct().ToArray(), cancellationToken);
        List<string> errors = [];
        for (int i = 0; i < rows.Count; i++)
        {
            ManualPositionRequest r = rows[i];
            string where = lineNumbers is null ? string.Empty : $"Line {lineNumbers[i]}: ";
            string asset = (r.Asset ?? string.Empty).Trim().ToUpperInvariant();
            void Fail(string message) => errors.Add(where + message);

            if (!Enum.IsDefined(r.Type))
            {
                Fail("the type must be Spot, Perp, Future, Option, Equity or Cash.");
                continue;
            }

            if (asset.Length is 0 or > 32 || !asset.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            {
                Fail("the asset is a ticker of up to 32 letters and digits, such as BTC or AAPL.");
                continue;
            }

            if (r.Quantity == 0m || Math.Abs(r.Quantity) > 1_000_000_000_000_000m)
            {
                Fail("the quantity cannot be zero; a short position is negative.");
            }

            if (r.Price is <= 0m || r.EntryPrice is <= 0m || r.UnderlyingPrice is <= 0m || r.Strike is <= 0m)
            {
                Fail("prices and the strike must be above zero.");
            }

            bool hasFeed = closes.ContainsKey(asset);
            switch (r.Type)
            {
                case InstrumentType.Option:
                    if (r.Expiry is null || r.Strike is null || r.OptionRight is null || r.Delta is null || r.Price is null)
                    {
                        Fail($"an option needs its expiry, strike, right (call or put), delta and price (the premium per unit).");
                    }
                    else if (r.Delta is < -1m or > 1m)
                    {
                        Fail("an option's delta is between −1 and 1.");
                    }
                    else if ((r.OptionRight == OptionRight.Put && r.Delta > 0m) || (r.OptionRight == OptionRight.Call && r.Delta < 0m))
                    {
                        // Many sources quote a put's delta without its sign; taken as written, a long put would show as long exposure.
                        Fail("a put's delta is zero or negative and a call's zero or positive. Give the delta of one option; whether the position is long or short is in its quantity.");
                    }

                    if (r.UnderlyingPrice is null && !hasFeed)
                    {
                        Fail($"{asset} has no price feed, so the option needs the price of its underlying.");
                    }

                    break;
                case InstrumentType.Future:
                    if (r.Expiry is null)
                    {
                        Fail("a future needs its expiry.");
                    }

                    goto default;
                case InstrumentType.Cash:
                    break;
                default:
                    if (r.Price is null && !hasFeed)
                    {
                        Fail($"{asset} has no price feed, so the position needs a price.");
                    }

                    break;
            }
        }

        return errors;
    }

    /// <summary>
    /// A position entered without a price relies on the asset's price feed. For an asset not seen before, its history
    /// is fetched here and now, so that entering "100 AAPL" works without typing a price or waiting for a scheduled run.
    /// </summary>
    private async Task LoadMissingHistoryAsync(IReadOnlyList<ManualPositionRequest> rows, CancellationToken cancellationToken)
    {
        DateOnly start = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-HistoryDays);
        var wanted = rows
            .Where(r => Enum.IsDefined(r.Type) && r.Type != InstrumentType.Cash && (r.Price is null || (r.Type == InstrumentType.Option && r.UnderlyingPrice is null)))
            .Select(r => (Symbol: (r.Asset ?? string.Empty).Trim().ToUpperInvariant(), Class: r.Type == InstrumentType.Equity ? AssetClass.Equity : AssetClass.Crypto))
            .Where(w => w.Symbol.Length is > 0 and <= 32 && w.Symbol.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            .Distinct()
            .Take(MaxHistoryLookups);
        foreach ((string symbol, AssetClass assetClass) in wanted)
        {
            try
            {
                await prices.EnsureAsync(symbol, assetClass, start, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The lookup is a convenience. Whatever went wrong with the feed, the position is judged on what is
                // already known, and the person is asked for a price if that is not enough.
            }
        }
    }

    private ManualPosition Apply(ManualPosition position, ManualPositionRequest request, string user)
    {
        bool option = request.Type == InstrumentType.Option;
        position.Type = request.Type;
        position.Asset = request.Asset.Trim().ToUpperInvariant();
        position.Quantity = Math.Round(request.Quantity, 8);
        position.Price = request.Price;
        position.EntryPrice = request.EntryPrice;
        // Fields that mean nothing for the type are dropped rather than stored and later mistaken for something.
        position.Expiry = request.Type is InstrumentType.Option or InstrumentType.Future ? request.Expiry : null;
        position.Strike = option ? request.Strike : null;
        position.OptionRight = option ? request.OptionRight : null;
        position.Delta = option ? request.Delta : null;
        position.UnderlyingPrice = option ? request.UnderlyingPrice : null;
        position.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()[..Math.Min(request.Note.Trim().Length, 500)];
        position.UpdatedAt = clock.GetUtcNow();
        position.UpdatedBy = user;
        return position;
    }
}

/// <summary>Reads the upload format: a header line, then one position per line, comma-separated, numbers with a decimal point.</summary>
public static class ManualCsv
{
    public static (List<ManualPositionRequest> Rows, List<int> LineNumbers, List<string> Errors) Parse(string csv)
    {
        List<ManualPositionRequest> rows = [];
        List<int> lineNumbers = [];
        List<string> errors = [];
        string[] lines = csv.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        int headerIndex = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l));
        if (headerIndex < 0)
        {
            return (rows, lineNumbers, ["The file is empty."]);
        }

        // A spreadsheet may add a byte-order mark and may use semicolons where the list separator is one.
        string headerLine = lines[headerIndex].TrimStart('﻿');
        char separator = headerLine.Count(c => c == ';') > headerLine.Count(c => c == ',') ? ';' : ',';
        List<string> header = Split(headerLine, separator).Select(h => h.Trim().ToLowerInvariant().Replace(' ', '_')).ToList();
        foreach (string required in new[] { "type", "asset", "quantity" })
        {
            if (!header.Contains(required))
            {
                errors.Add($"The first line must name the columns; \"{required}\" is missing. Expected: {ManualPositionService.CsvHeader}");
            }
        }

        if (errors.Count > 0)
        {
            return (rows, lineNumbers, errors);
        }

        for (int i = headerIndex + 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            if (rows.Count >= ManualPositionService.MaxUploadLines)
            {
                errors.Add($"The file has more than {ManualPositionService.MaxUploadLines} positions.");
                break;
            }

            List<string> cells = Split(lines[i], separator);
            string Cell(string name) => header.IndexOf(name) is int index and >= 0 && index < cells.Count ? cells[index].Trim() : string.Empty;
            int line = i + 1;
            List<string> problems = [];
            if (cells.Count > header.Count)
            {
                // A stray comma shifts every value after it into the wrong column: 1,500 shares would load as 1 share at 500.
                problems.Add($"it has {cells.Count} values but the heading has {header.Count} (a number must not contain a comma)");
            }

            decimal? Number(string name)
            {
                string text = Cell(name);
                if (text.Length == 0)
                {
                    return null;
                }

                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
                {
                    return value;
                }

                problems.Add($"\"{text}\" in {name} is not a number (use a decimal point and no thousands separators)");
                return null;
            }

            // A cell holding two names with a comma between them would be read as a mix of both.
            if (!Enum.TryParse(Cell("type"), ignoreCase: true, out InstrumentType type) || !Enum.IsDefined(type) || int.TryParse(Cell("type"), out _) || Cell("type").Contains(','))
            {
                problems.Add($"\"{Cell("type")}\" is not a type (Spot, Perp, Future, Option, Equity or Cash)");
            }

            DateOnly? expiry = null;
            if (Cell("expiry").Length > 0)
            {
                if (DateOnly.TryParseExact(Cell("expiry"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
                {
                    expiry = parsed;
                }
                else
                {
                    problems.Add($"\"{Cell("expiry")}\" is not a date written as 2026-12-25");
                }
            }

            OptionRight? right = Cell("right").ToLowerInvariant() switch
            {
                "" => null,
                "call" or "c" => OptionRight.Call,
                "put" or "p" => OptionRight.Put,
                _ => (OptionRight)0,
            };
            if (right == 0)
            {
                problems.Add($"\"{Cell("right")}\" is not call or put");
                right = null;
            }

            decimal? quantity = Number("quantity");
            if (quantity is null && Cell("quantity").Length == 0)
            {
                problems.Add("the quantity is missing");
            }

            ManualPositionRequest row = new(type, Cell("asset"), quantity ?? 0m, Number("price"), Number("entry_price"), expiry, Number("strike"), right, Number("delta"), Number("underlying_price"), Cell("note").Length == 0 ? null : Cell("note"));
            if (problems.Count > 0)
            {
                errors.Add($"Line {line}: {string.Join("; ", problems)}.");
                continue;
            }

            rows.Add(row);
            lineNumbers.Add(line);
        }

        if (errors.Count == 0 && rows.Count == 0)
        {
            errors.Add("The file has a header but no positions. To empty the book, remove the positions on the screen.");
        }

        return (rows, lineNumbers, errors);
    }

    /// <summary>Splits one line, honouring double quotes around a cell that contains the separator.</summary>
    private static List<string> Split(string line, char separator)
    {
        List<string> cells = [];
        System.Text.StringBuilder cell = new();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == separator && !quoted)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }
}
