using FluentValidation;
using KatalepsisFT.Domain.Funds;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Structure;

public sealed record NewManagerRequest(string Name, decimal AllocatedCapitalUsd);

public sealed record NewSleeveRequest(string Name, MandateType MandateType, IReadOnlyList<NewManagerRequest> Managers);

public sealed record NewFundRequest(string Name, int? BenchmarkId, IReadOnlyList<NewSleeveRequest> Sleeves);

public sealed record FundCreatedDto(int Id, string Name);

public sealed class NewFundRequestValidator : AbstractValidator<NewFundRequest>
{
    public NewFundRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Sleeves).NotEmpty().WithMessage("A fund needs at least one sleeve.");
        RuleFor(x => x.Sleeves).Must(s => s.Select(x => x.Name.Trim().ToUpperInvariant()).Distinct().Count() == s.Count).WithMessage("Sleeve names must differ.");
        RuleForEach(x => x.Sleeves)
            .ChildRules(sleeve =>
            {
                sleeve.RuleFor(s => s.Name).NotEmpty().MaximumLength(200);
                sleeve.RuleFor(s => s.MandateType).IsInEnum();
                sleeve.RuleFor(s => s.Managers).NotEmpty().WithMessage("A sleeve needs at least one manager.");
                sleeve
                    .RuleForEach(s => s.Managers)
                    .ChildRules(manager =>
                    {
                        manager.RuleFor(m => m.Name).NotEmpty().MaximumLength(200);
                        manager.RuleFor(m => m.AllocatedCapitalUsd).InclusiveBetween(0m, 1_000_000_000_000m);
                    });
            });
    }
}

/// <summary>Creates real funds. Their managers take positions from venue connections.</summary>
public sealed class StructureService(IFtDbContext database, TimeProvider clock)
{
    /// <returns>The new fund, or the reason it was refused.</returns>
    public async Task<(FundCreatedDto? Fund, string? Error)> CreateFundAsync(NewFundRequest request, CancellationToken cancellationToken)
    {
        string name = request.Name.Trim();
        if (await database.Funds.AnyAsync(f => f.Name == name, cancellationToken))
        {
            return (null, "A fund with that name already exists.");
        }

        if (request.BenchmarkId is int benchmarkId && !await database.Benchmarks.AnyAsync(b => b.Id == benchmarkId, cancellationToken))
        {
            return (null, "That benchmark does not exist.");
        }

        DateTimeOffset now = clock.GetUtcNow();
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        Fund fund = new()
        {
            Name = name,
            BenchmarkId = request.BenchmarkId,
            CreatedAt = now,
            Sleeves = request
                .Sleeves.Select(
                    (sleeve, index) =>
                        new Sleeve
                        {
                            Name = sleeve.Name.Trim(),
                            MandateType = sleeve.MandateType,
                            SortOrder = index + 1,
                            Managers = sleeve
                                .Managers.Select(m => new ManagerAccount
                                {
                                    Name = m.Name.Trim(),
                                    AllocatedCapitalUsd = m.AllocatedCapitalUsd,
                                    InceptionDate = today,
                                    DataSource = ManagerDataSource.Venue,
                                })
                                .ToList(),
                        }
                )
                .ToList(),
        };
        database.Funds.Add(fund);
        await database.SaveChangesAsync(cancellationToken);
        return (new FundCreatedDto(fund.Id, fund.Name), null);
    }
}
