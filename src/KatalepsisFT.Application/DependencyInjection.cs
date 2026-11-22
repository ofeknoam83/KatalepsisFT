using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace KatalepsisFT.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
        services.AddScoped<Performance.PerformanceService>();
        services.AddScoped<Risk.RiskService>();
        services.AddScoped<Alerts.AlertEvaluator>();
        services.AddScoped<Alerts.AlertService>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
