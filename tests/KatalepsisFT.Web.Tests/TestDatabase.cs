using KatalepsisFT.Domain.Access;
using KatalepsisFT.Infrastructure.Postgres;
using KatalepsisFT.Persistence;
using KatalepsisFT.Persistence.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace KatalepsisFT.Web.Tests;

/// <summary>
/// A real Postgres database created for one test class and dropped afterwards. TEST_DATABASE_URL must point at a server
/// where the user may create databases, for example postgresql://postgres:postgres@localhost:5432/postgres.
/// </summary>
public sealed class TestDatabase : IAsyncLifetime
{
    public const string Password = "Correct-Horse-9-Battery";
    public const string MasterKey = "test-master-key-0123456789-abcdefghijklmnop";

    private readonly string _serverUrl =
        Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
        ?? throw new InvalidOperationException("Set TEST_DATABASE_URL to a Postgres server where the user may create databases.");

    private readonly string _name = $"kft_test_{Guid.NewGuid():N}";
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public string Url { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await ExecuteOnServerAsync($"create database {_name}");
        Uri server = new(_serverUrl);
        Url = new UriBuilder(server) { Path = "/" + _name }.Uri.ToString();

        using IServiceScope scope = App.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FtDbContext>().Database.MigrateAsync();
        InitialAdminSeeder seeder = ActivatorUtilities.CreateInstance<InitialAdminSeeder>(scope.ServiceProvider);
        await seeder.EnsureRolesAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (WebApplicationFactory<Program> factory in _factories)
        {
            await factory.DisposeAsync();
        }

        NpgsqlConnection.ClearAllPools();
        try
        {
            await ExecuteOnServerAsync($"drop database if exists {_name} with (force)");
        }
        catch (PostgresException e) when (e.SqlState == "42501")
        {
            // Postgres' own housekeeping was working in the database, and only a superuser may interrupt that.
            // It is done within seconds; if not, a leftover test database harms nothing.
            await Task.Delay(TimeSpan.FromSeconds(3));
            try
            {
                await ExecuteOnServerAsync($"drop database if exists {_name} with (force)");
            }
            catch (PostgresException)
            {
            }
        }
    }

    private WebApplicationFactory<Program>? _app;

    /// <summary>The web service on this database, shared by the tests of a class. Starting one per test is slow and makes EF Core
    /// complain about the number of data sources.</summary>
    public WebApplicationFactory<Program> App => _app ??= CreateApp();

    /// <summary>A separate instance of the web service on this database, like one of the two instances on Render.</summary>
    public WebApplicationFactory<Program> CreateApp(string? databaseUrl = null, Action<IServiceCollection>? services = null)
    {
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DATABASE_URL", databaseUrl ?? Url);
            builder.UseSetting("MASTER_KEY", MasterKey);
            // No test may call a real price service; a test that needs prices registers its own source.
            builder.ConfigureServices(all =>
            {
                foreach (ServiceDescriptor descriptor in all.Where(s => s.ServiceType == typeof(KatalepsisFT.Application.Prices.IDailyPriceSource)).ToList())
                {
                    all.Remove(descriptor);
                }
            });
            if (services is not null)
            {
                builder.ConfigureServices(services);
            }
        });
        _factories.Add(factory);
        return factory;
    }

    public static HttpClient CreateClient(WebApplicationFactory<Program> app)
    {
        // https, because the session cookie is marked Secure and would not be sent back over http.
        HttpClient client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-KFT-Request", "1");
        return client;
    }

    public async Task<string> AddUserAsync(WebApplicationFactory<Program> app, string role, bool mustChangePassword = false)
    {
        using IServiceScope scope = app.Services.CreateScope();
        UserManager<AppUser> users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        string email = $"{Guid.NewGuid():N}@example.com";
        AppUser user = new()
        {
            UserName = email,
            Email = email,
            DisplayName = "Test User",
            MustChangePassword = mustChangePassword,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        IdentityResult created = await users.CreateAsync(user, Password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        await users.AddToRoleAsync(user, role);
        return email;
    }

    /// <summary>Whether the demo fund has already been seeded into this database by an earlier test of the class.</summary>
    public async Task<bool> IsSeededAsync(WebApplicationFactory<Program> app)
    {
        using IServiceScope scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FtDbContext>().Funds.AnyAsync();
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using NpgsqlConnection connection = new(PostgresConnection.FromUrl(_serverUrl));
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
