using KatalepsisFT.Analytics.Holdings;
using KatalepsisFT.Domain.Funds;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Holdings;

public sealed record GetFundsQuery : IRequest<IReadOnlyList<FundListItemDto>>;

public sealed record GetFundOverviewQuery(int FundId, InstrumentFilter Filter) : IRequest<FundOverviewDto?>;

public sealed record GetFundHoldingsQuery(int FundId, InstrumentFilter Filter) : IRequest<FundHoldingsDto?>;

public sealed record GetSleeveQuery(int SleeveId, InstrumentFilter Filter) : IRequest<SleeveDetailDto?>;

public sealed record GetManagerQuery(int ManagerId, InstrumentFilter Filter) : IRequest<ManagerDetailDto?>;

public sealed record GetAssetQuery(int FundId, string Symbol) : IRequest<AssetDetailDto?>;

/// <summary>Shared projection from aggregates to what the screens show.</summary>
internal static class HoldingsProjection
{
    public const int TopAssetCount = 8;

    public static IReadOnlyList<AssetRowDto> Assets(FundBook book, IEnumerable<HoldingRow> rows, IReadOnlyDictionary<string, AssetPriceDto> prices)
    {
        Dictionary<int, string> names = book.Managers.ToDictionary(m => m.Id, m => m.Name);
        return HoldingsAggregator
            .ByAsset(rows)
            .Select(a => new AssetRowDto(
                prices.TryGetValue(a.Asset, out AssetPriceDto? price) ? price : new AssetPriceDto(a.Asset, a.Asset, null, null, null, null, null),
                a.LongUsd,
                a.ShortUsd,
                a.GrossUsd,
                a.NetUsd,
                a.NetByType,
                a.NetByManager.OrderBy(m => m.Key).Select(m => new HolderDto(m.Key, names[m.Key], m.Value)).ToList(),
                a.SleeveIds,
                a.HasOffsettingManagers
            ))
            .ToList();
    }

    public static IReadOnlyList<TypeExposureDto> Types(IEnumerable<HoldingRow> rows) =>
        HoldingsAggregator.ByType(rows).Select(t => new TypeExposureDto(t.Type, t.LongUsd, t.ShortUsd, t.GrossUsd, t.NetUsd)).ToList();

    public static string[] Symbols(IEnumerable<HoldingRow> rows) => rows.Select(r => r.Asset).Distinct().ToArray();
}

public sealed class GetFundsHandler(IFtDbContext database) : IRequestHandler<GetFundsQuery, IReadOnlyList<FundListItemDto>>
{
    public async Task<IReadOnlyList<FundListItemDto>> Handle(GetFundsQuery request, CancellationToken cancellationToken)
    {
        var funds = await database
            .Funds.AsNoTracking()
            .OrderBy(f => f.IsDemo)
            .ThenBy(f => f.Name)
            .Select(f => new
            {
                f.Id,
                f.Name,
                f.IsDemo,
                f.Kind,
                f.OwnerName,
                Sleeves = f.Sleeves.Count,
                Managers = f.Sleeves.SelectMany(s => s.Managers).Count(),
            })
            .ToListAsync(cancellationToken);

        List<FundListItemDto> result = [];
        FundBookReader reader = new(database);
        foreach (var fund in funds)
        {
            FundBook? book = await reader.LoadAsync(fund.Id, cancellationToken);
            bool valued = book is not null && book.Rows.Count > 0;
            result.Add(new FundListItemDto(fund.Id, fund.Name, fund.IsDemo, valued ? book!.Rows.Sum(r => r.MarketValueUsd) : 0m, valued ? book!.AsOf : null, fund.Sleeves, fund.Managers, fund.Kind, fund.OwnerName));
        }

        return result;
    }
}

public sealed class GetFundOverviewHandler(IFtDbContext database) : IRequestHandler<GetFundOverviewQuery, FundOverviewDto?>
{
    public async Task<FundOverviewDto?> Handle(GetFundOverviewQuery request, CancellationToken cancellationToken)
    {
        FundBookReader reader = new(database);
        FundBook? book = await reader.LoadAsync(request.FundId, cancellationToken);
        if (book is null)
        {
            return null;
        }

        List<HoldingRow> exposure = FundBook.Apply(book.Rows, request.Filter).ToList();
        var prices = await reader.LoadPricesAsync(HoldingsProjection.Symbols(exposure), book.AsOf, cancellationToken);
        List<SleeveRowDto> sleeves = book
            .Sleeves.Select(s => new SleeveRowDto(
                FundBook.Ref(s),
                book.Managers.Count(m => m.SleeveId == s.Id),
                FundBook.Totals(book.Rows.Where(r => r.SleeveId == s.Id).ToList(), request.Filter)
            ))
            .ToList();

        return new FundOverviewDto(
            book.FundRef,
            book.AsOf,
            FundBook.Totals(book.Rows, request.Filter),
            sleeves,
            HoldingsProjection.Assets(book, exposure, prices).Take(HoldingsProjection.TopAssetCount).ToList(),
            HoldingsProjection.Types(book.Rows)
        );
    }
}

public sealed class GetFundHoldingsHandler(IFtDbContext database) : IRequestHandler<GetFundHoldingsQuery, FundHoldingsDto?>
{
    public async Task<FundHoldingsDto?> Handle(GetFundHoldingsQuery request, CancellationToken cancellationToken)
    {
        FundBookReader reader = new(database);
        FundBook? book = await reader.LoadAsync(request.FundId, cancellationToken);
        if (book is null)
        {
            return null;
        }

        List<HoldingRow> exposure = FundBook.Apply(book.Rows, request.Filter).ToList();
        var prices = await reader.LoadPricesAsync(HoldingsProjection.Symbols(exposure), book.AsOf, cancellationToken);
        return new FundHoldingsDto(book.FundRef, book.AsOf, FundBook.Totals(book.Rows, request.Filter), book.Sleeves.Select(FundBook.Ref).ToList(), HoldingsProjection.Assets(book, exposure, prices));
    }
}

public sealed class GetSleeveHandler(IFtDbContext database) : IRequestHandler<GetSleeveQuery, SleeveDetailDto?>
{
    public async Task<SleeveDetailDto?> Handle(GetSleeveQuery request, CancellationToken cancellationToken)
    {
        int? fundId = await database.Sleeves.Where(s => s.Id == request.SleeveId).Select(s => (int?)s.FundId).FirstOrDefaultAsync(cancellationToken);
        if (fundId is null)
        {
            return null;
        }

        FundBookReader reader = new(database);
        FundBook book = (await reader.LoadAsync(fundId.Value, cancellationToken))!;
        Sleeve sleeve = book.Sleeves.Single(s => s.Id == request.SleeveId);
        List<HoldingRow> rows = book.Rows.Where(r => r.SleeveId == sleeve.Id).ToList();
        List<HoldingRow> exposure = FundBook.Apply(rows, request.Filter).ToList();
        var prices = await reader.LoadPricesAsync(HoldingsProjection.Symbols(exposure), book.AsOf, cancellationToken);

        return new SleeveDetailDto(
            book.FundRef,
            FundBook.Ref(sleeve),
            book.AsOf,
            book.Rows.Sum(r => r.MarketValueUsd),
            FundBook.Totals(rows, request.Filter),
            sleeve.Limits.OrderBy(l => l.Kind).Select(l => new MandateLimitDto(l.Kind, l.Value)).ToList(),
            book.Managers.Where(m => m.SleeveId == sleeve.Id)
                .Select(m => new ManagerRowDto(FundBook.Ref(m), m.DataSource, FundBook.Totals(rows.Where(r => r.ManagerId == m.Id).ToList(), request.Filter)))
                .ToList(),
            HoldingsProjection.Assets(book, exposure, prices),
            HoldingsProjection.Types(rows),
            HoldingsAggregator.Overlaps(exposure).Select(o => new OverlapPairDto(o.ManagerA, o.ManagerB, o.Share, o.OppositeSides)).ToList()
        );
    }
}

public sealed class GetManagerHandler(IFtDbContext database) : IRequestHandler<GetManagerQuery, ManagerDetailDto?>
{
    public async Task<ManagerDetailDto?> Handle(GetManagerQuery request, CancellationToken cancellationToken)
    {
        int? fundId = await database.ManagerAccounts.Where(m => m.Id == request.ManagerId).Select(m => (int?)m.Sleeve.FundId).FirstOrDefaultAsync(cancellationToken);
        if (fundId is null)
        {
            return null;
        }

        FundBookReader reader = new(database);
        FundBook book = (await reader.LoadAsync(fundId.Value, cancellationToken))!;
        ManagerAccount manager = book.Managers.Single(m => m.Id == request.ManagerId);
        Sleeve sleeve = book.Sleeves.Single(s => s.Id == manager.SleeveId);
        List<HoldingRow> rows = book.Rows.Where(r => r.ManagerId == manager.Id).ToList();
        List<HoldingRow> exposure = FundBook.Apply(rows, request.Filter).ToList();
        var prices = await reader.LoadPricesAsync(HoldingsProjection.Symbols(exposure), book.AsOf, cancellationToken);

        return new ManagerDetailDto(
            book.FundRef,
            FundBook.Ref(sleeve),
            FundBook.Ref(manager),
            manager.DataSource,
            manager.InceptionDate,
            book.AsOf,
            book.Rows.Where(r => r.SleeveId == sleeve.Id).Sum(r => r.MarketValueUsd),
            book.Rows.Sum(r => r.MarketValueUsd),
            FundBook.Totals(rows, request.Filter),
            rows.OrderBy(r => r.Type == Domain.Instruments.InstrumentType.Cash)
                .ThenByDescending(r => Math.Abs(r.ExposureUsd))
                .Select(r => new PositionDto(r.Asset, r.Instrument, r.Type, r.Quantity, r.EntryPrice, r.MarkPrice, r.MarketValueUsd, r.ExposureUsd, r.UnrealizedPnlUsd))
                .ToList(),
            HoldingsProjection.Assets(book, exposure, prices)
        );
    }
}

public sealed class GetAssetHandler(IFtDbContext database) : IRequestHandler<GetAssetQuery, AssetDetailDto?>
{
    public async Task<AssetDetailDto?> Handle(GetAssetQuery request, CancellationToken cancellationToken)
    {
        FundBookReader reader = new(database);
        FundBook? book = await reader.LoadAsync(request.FundId, cancellationToken);
        string symbol = request.Symbol.Trim().ToUpperInvariant();
        if (book is null || !await database.Assets.AnyAsync(a => a.Symbol == symbol, cancellationToken))
        {
            return null;
        }

        List<HoldingRow> rows = book.Rows.Where(r => r.Asset == symbol && r.Type != Domain.Instruments.InstrumentType.Cash).ToList();
        var prices = await reader.LoadPricesAsync([symbol], book.AsOf, cancellationToken);
        BookTotals totals = HoldingsAggregator.Totals(rows);
        Dictionary<int, decimal> managerNav = book.Rows.GroupBy(r => r.ManagerId).ToDictionary(g => g.Key, g => g.Sum(r => r.MarketValueUsd));
        Dictionary<int, ManagerAccount> managers = book.Managers.ToDictionary(m => m.Id);

        // Every sleeve is listed, also those that do not hold the asset: "not held" is an answer too.
        List<AssetSleeveDto> sleeves = book
            .Sleeves.Select(s =>
            {
                List<HoldingRow> inSleeve = rows.Where(r => r.SleeveId == s.Id).ToList();
                BookTotals sleeveTotals = HoldingsAggregator.Totals(inSleeve);
                return new AssetSleeveDto(
                    FundBook.Ref(s),
                    book.Rows.Where(r => r.SleeveId == s.Id).Sum(r => r.MarketValueUsd),
                    sleeveTotals.LongUsd,
                    sleeveTotals.ShortUsd,
                    inSleeve
                        .OrderBy(r => r.ManagerId)
                        .ThenBy(r => r.Type)
                        .Select(r => new AssetPositionDto(FundBook.Ref(managers[r.ManagerId]), managerNav[r.ManagerId], r.Instrument, r.Type, r.Quantity, r.ExposureUsd))
                        .ToList()
                );
            })
            .ToList();

        return new AssetDetailDto(
            book.FundRef,
            prices.TryGetValue(symbol, out AssetPriceDto? price) ? price : new AssetPriceDto(symbol, symbol, null, null, null, null, null),
            book.AsOf,
            book.Rows.Sum(r => r.MarketValueUsd),
            totals.LongUsd,
            totals.ShortUsd,
            totals.GrossUsd,
            totals.NetUsd,
            rows.Select(r => r.ManagerId).Distinct().Count(),
            sleeves,
            HoldingsProjection.Types(rows)
        );
    }
}
