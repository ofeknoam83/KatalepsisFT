using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace KatalepsisFT.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
        services.AddSingleton<Prices.TodayPrices>();
        services.AddScoped<Performance.PerformanceService>();
        services.AddScoped<Risk.RiskService>();
        services.AddScoped<Risk.VenueExposureService>();
        services.AddScoped<Connections.VenueAdapterRegistry>();
        services.AddScoped<Connections.ManagerBookWriter>();
        services.AddScoped<Live.IntradayService>();
        services.AddScoped<Connections.ManagerBookService>();
        services.AddScoped<Connections.ConnectionSyncService>();
        services.AddScoped<Prices.PriceBackfillService>();
        services.AddScoped<Manual.ManualPositionService>();
        services.AddScoped<History.TrackRecordService>();
        services.AddScoped<Connections.ConnectionService>();
        services.AddScoped<Structure.StructureService>();
        services.AddScoped<Structure.RenameService>();
        services.AddScoped<Alerts.AlertEvaluator>();
        services.AddScoped<Alerts.AlertService>();
        services.AddScoped<Audit.AuditLog>();
        services.AddScoped<Reports.ReportService>();
        services.AddScoped<Reports.ReportScheduleService>();
        services.AddScoped<Execution.TradingService>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
