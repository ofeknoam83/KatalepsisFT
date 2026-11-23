using KatalepsisFT.Persistence.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace KatalepsisFT.Persistence;

public static class DependencyInjection
{
    public const int MinimumPasswordLength = 12;

    /// <summary>Registers the EF Core context on the already registered <see cref="NpgsqlDataSource"/>.</summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        services.AddDbContext<FtDbContext>((provider, options) => Configure(options, provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<KatalepsisFT.Application.IFtDbContext>(provider => provider.GetRequiredService<FtDbContext>());
        return services;
    }

    /// <summary>User and role management with the system's password and lockout rules. The web service adds sign-in on top.</summary>
    public static IdentityBuilder AddFtIdentityCore(this IServiceCollection services)
    {
        return services
            .AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = MinimumPasswordLength;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<IdentityRole<int>>()
            .AddEntityFrameworkStores<FtDbContext>();
    }

    internal static void Configure(DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options
            .UseNpgsql(dataSource)
            .UseSnakeCaseNamingConvention()
            // EF keeps one set of internals per data source and complains past twenty of them. A service has one
            // data source; the tests start many services in one process, each with its own, and that is all this is.
            .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
}
