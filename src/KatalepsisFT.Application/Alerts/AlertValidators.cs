using FluentValidation;
using KatalepsisFT.Domain.Alerts;

namespace KatalepsisFT.Application.Alerts;

public sealed class SaveAlertRuleRequestValidator : AbstractValidator<SaveAlertRuleRequest>
{
    public SaveAlertRuleRequestValidator()
    {
        RuleFor(x => x.ScopeKind).IsInEnum();
        RuleFor(x => x.Metric).IsInEnum();
        RuleFor(x => x.Operator).IsInEnum();
        RuleFor(x => x.Severity).IsInEnum();
        RuleFor(x => x.ScopeId).GreaterThan(0);
        // Ratios are fractions of NAV (10 = 1,000%); days to liquidate are days.
        RuleFor(x => x.Threshold).InclusiveBetween(-10m, 10m).When(x => x.Metric != AlertMetric.SlowestDaysToLiquidate).WithMessage("The threshold must be between −1,000% and 1,000%.");
        RuleFor(x => x.Threshold).InclusiveBetween(0m, 3650m).When(x => x.Metric == AlertMetric.SlowestDaysToLiquidate).WithMessage("The threshold must be between 0 and 3,650 days.");
    }
}

public sealed class SnoozeRequestValidator : AbstractValidator<SnoozeRequest>
{
    public SnoozeRequestValidator()
    {
        RuleFor(x => x.Days).InclusiveBetween(1, AlertService.MaxSnoozeDays).WithMessage($"Snooze for 1 to {AlertService.MaxSnoozeDays} days.");
        RuleFor(x => x.Note).MaximumLength(1000);
    }
}

public sealed class NoteRequestValidator : AbstractValidator<NoteRequest>
{
    public NoteRequestValidator()
    {
        RuleFor(x => x.Note).MaximumLength(1000);
    }
}
