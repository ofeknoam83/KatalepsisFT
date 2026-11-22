using KatalepsisFT.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace KatalepsisFT.Persistence;

/// <summary>Used only by the dotnet-ef tool when a migration is added. No connection is opened for that.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<FtDbContext>
{
    public FtDbContext CreateDbContext(string[] args)
    {
        string? configured = Environment.GetEnvironmentVariable("DATABASE_URL");
        string url = string.IsNullOrWhiteSpace(configured) ? "postgresql://design:design@localhost/design" : configured;
        DbContextOptionsBuilder<FtDbContext> options = new();
        DependencyInjection.Configure(options, NpgsqlDataSource.Create(PostgresConnection.FromUrl(url)));
        return new FtDbContext(options.Options);
    }
}
