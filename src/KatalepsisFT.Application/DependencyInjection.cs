using Microsoft.Extensions.DependencyInjection;

namespace KatalepsisFT.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
        services.AddScoped<Performance.PerformanceService>();
        return services;
    }
}
