using KatalepsisFT.Application.Connections;
using KatalepsisFT.Infrastructure;
using KatalepsisFT.Persistence;
using KatalepsisFT.Venues;
using KatalepsisFT.Worker;
using KatalepsisFT.Infrastructure.Health;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Each Render worker service is started with its group: --group cefi | defi | core
string? group = builder.Configuration["group"];
if (group is null || !WorkerGroups.All.Contains(group, StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Start the worker with --group followed by one of: {string.Join(", ", WorkerGroups.All)}.");
    return 2;
}

(bool hasPostgres, bool hasKeyValue) = builder.Services.AddStores(builder.Configuration);
if (!hasKeyValue)
{
    Console.Error.WriteLine($"{KatalepsisFT.Infrastructure.DependencyInjection.KeyValueUrlKey} is not set; the worker has nothing to connect to.");
    return 2;
}

// Outgoing request logs print full addresses. Nothing secret is put in an address, and this keeps it that way if one ever is.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.AddSingleton(new WorkerIdentity(group.ToLowerInvariant()));
builder.Services.AddHostedService<HeartbeatPublisher>();

// The venue workers read their connections from Postgres and write the managers' books back to it.
if (hasPostgres && group.ToLowerInvariant() is WorkerGroups.Cefi or WorkerGroups.Defi)
{
    // One line per SQL statement every minute would bury everything else.
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
    builder.Services.AddPersistence();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddCredentialVault(builder.Configuration);
    builder.Services.AddVenueAdapters();
    builder.Services.AddScoped<VenueAdapterRegistry>();
    builder.Services.AddScoped<ManagerBookWriter>();
    builder.Services.AddScoped<KatalepsisFT.Application.Risk.RiskService>();
    builder.Services.AddScoped<KatalepsisFT.Application.Risk.VenueExposureService>();
    builder.Services.AddScoped<KatalepsisFT.Application.Alerts.AlertEvaluator>();
    builder.Services.AddScoped<KatalepsisFT.Application.Live.IntradayService>();
    builder.Services.AddSingleton<KatalepsisFT.Application.Live.IFundChangeNotifier, KatalepsisFT.Infrastructure.KeyValue.KeyValueFundChangeNotifier>();
    builder.Services.AddScoped<ManagerBookService>();
    builder.Services.AddScoped<ConnectionSyncService>();
    builder.Services.AddHostedService<ConnectionSyncWorker>();
}

// The core worker keeps price history complete, so a newly seen asset gets real risk figures within minutes.
if (hasPostgres && group.ToLowerInvariant() is WorkerGroups.Core)
{
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
    builder.Services.AddPersistence();
    builder.Services.AddMarketData();
    builder.Services.AddScoped<KatalepsisFT.Application.Prices.PriceBackfillService>();
    builder.Services.AddScoped<ManagerBookWriter>();
    builder.Services.AddScoped<KatalepsisFT.Application.Risk.RiskService>();
    builder.Services.AddScoped<KatalepsisFT.Application.Risk.VenueExposureService>();
    builder.Services.AddScoped<KatalepsisFT.Application.Alerts.AlertEvaluator>();
    builder.Services.AddScoped<KatalepsisFT.Application.Live.IntradayService>();
    builder.Services.AddSingleton<KatalepsisFT.Application.Live.IFundChangeNotifier, KatalepsisFT.Infrastructure.KeyValue.KeyValueFundChangeNotifier>();
    builder.Services.AddScoped<ManagerBookService>();
    builder.Services.AddHostedService<PriceHistoryWorker>();
    builder.Services.AddScoped<KatalepsisFT.Application.Demo.DemoLiveService>();
    builder.Services.AddHostedService<DemoLiveWorker>();
    // Reports that go out by email on their schedule.
    builder.Services.AddMail();
    builder.Services.AddScoped<KatalepsisFT.Application.Performance.PerformanceService>();
    builder.Services.AddScoped<KatalepsisFT.Application.Alerts.AlertService>();
    builder.Services.AddScoped<KatalepsisFT.Application.Reports.ReportService>();
    builder.Services.AddScoped<KatalepsisFT.Application.Reports.ReportScheduleService>();
    builder.Services.AddHostedService<ReportWorker>();
}

// The execution service: market data, the order engine and the trading-key vault. Nothing else holds EXEC_MASTER_KEY.
if (hasPostgres && group.ToLowerInvariant() is WorkerGroups.Exec)
{
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
    builder.Services.AddPersistence();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(ExecutionOptions.From(builder.Configuration));
    builder.Services.AddSingleton<KatalepsisFT.Application.Execution.ConsolidatedBook>();
    builder.Services.AddSingleton<KatalepsisFT.Application.Execution.VenueStatusBoard>();
    builder.Services.AddScoped<KatalepsisFT.Application.Execution.ExecutionEngine>();
    builder.Services.AddHttpClient("exec", client => client.Timeout = TimeSpan.FromSeconds(10));
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream, KatalepsisFT.Infrastructure.Execution.BinanceBookStream>();
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream, KatalepsisFT.Infrastructure.Execution.BybitBookStream>();
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream, KatalepsisFT.Infrastructure.Execution.OkxBookStream>();
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream, KatalepsisFT.Infrastructure.Execution.HyperliquidBookStream>();
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream, KatalepsisFT.Infrastructure.Execution.KucoinSpotBookStream>();
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream, KatalepsisFT.Infrastructure.Execution.KucoinPerpBookStream>();
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream, KatalepsisFT.Infrastructure.Execution.BinanceSpotBookStream>();
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream, KatalepsisFT.Infrastructure.Execution.BybitSpotBookStream>();
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream, KatalepsisFT.Infrastructure.Execution.OkxSpotBookStream>();
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream>(new KatalepsisFT.Infrastructure.Execution.BitgetBookStream(KatalepsisFT.Domain.Instruments.InstrumentType.Spot));
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream>(new KatalepsisFT.Infrastructure.Execution.BitgetBookStream(KatalepsisFT.Domain.Instruments.InstrumentType.Perp));
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream, KatalepsisFT.Infrastructure.Execution.GateSpotBookStream>();
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream, KatalepsisFT.Infrastructure.Execution.GatePerpBookStream>();
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream, KatalepsisFT.Infrastructure.Execution.MexcPerpBookStream>();
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream, KatalepsisFT.Infrastructure.Execution.CoinbaseSpotBookStream>();
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream, KatalepsisFT.Infrastructure.Execution.KrakenSpotBookStream>();
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookStream, KatalepsisFT.Infrastructure.Execution.DeribitPerpBookStream>();
    builder.Services.AddSingleton<KatalepsisFT.Infrastructure.Execution.BookPoller, KatalepsisFT.Infrastructure.Execution.MexcSpotBookPoller>();
    builder.Services.AddSingleton(provider => { ExecutionOptions options = provider.GetRequiredService<ExecutionOptions>(); return new KatalepsisFT.Infrastructure.Execution.BookDemand(options.Assets, options.MostBooks); });
    // The venues' instrument lists are large; they get a client of their own with a longer wait.
    builder.Services.AddHttpClient("lists", client => client.Timeout = TimeSpan.FromSeconds(40));
    builder.Services.AddHostedService<InstrumentListWorker>();
    builder.Services.AddHostedService<BookDemandWorker>();
    builder.Services.AddHostedService<MarketDataWorker>();
    // Real orders. The gateway and the opened trading keys exist only in this service.
    builder.Services.AddSingleton<OpenedTradingKeys>();
    builder.Services.AddSingleton<KatalepsisFT.Application.Execution.ITradingKeys>(provider => provider.GetRequiredService<OpenedTradingKeys>());
    builder.Services.AddSingleton<KatalepsisFT.Application.Execution.LivePacing>();
    builder.Services.AddHttpClient(KatalepsisFT.Venues.Kucoin.KucoinOrderGateway.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(8));
    builder.Services.AddSingleton<KatalepsisFT.Application.Execution.IOrderGateway>(provider =>
        new KatalepsisFT.Venues.Kucoin.KucoinOrderGateway(provider.GetRequiredService<IHttpClientFactory>().CreateClient(KatalepsisFT.Venues.Kucoin.KucoinOrderGateway.HttpClientName), provider.GetRequiredService<TimeProvider>()));
    builder.Services.AddHttpClient(KatalepsisFT.Venues.Binance.BinanceOrderGateway.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(8));
    builder.Services.AddSingleton<KatalepsisFT.Application.Execution.IOrderGateway>(provider =>
        new KatalepsisFT.Venues.Binance.BinanceOrderGateway(provider.GetRequiredService<IHttpClientFactory>().CreateClient(KatalepsisFT.Venues.Binance.BinanceOrderGateway.HttpClientName), provider.GetRequiredService<TimeProvider>()));
    builder.Services.AddHttpClient(KatalepsisFT.Venues.Bybit.BybitOrderGateway.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(8));
    builder.Services.AddSingleton<KatalepsisFT.Application.Execution.IOrderGateway>(provider =>
        new KatalepsisFT.Venues.Bybit.BybitOrderGateway(provider.GetRequiredService<IHttpClientFactory>().CreateClient(KatalepsisFT.Venues.Bybit.BybitOrderGateway.HttpClientName), provider.GetRequiredService<TimeProvider>()));
    builder.Services.AddHttpClient(KatalepsisFT.Venues.Okx.OkxOrderGateway.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(8));
    builder.Services.AddSingleton<KatalepsisFT.Application.Execution.IOrderGateway>(provider =>
        new KatalepsisFT.Venues.Okx.OkxOrderGateway(provider.GetRequiredService<IHttpClientFactory>().CreateClient(KatalepsisFT.Venues.Okx.OkxOrderGateway.HttpClientName), provider.GetRequiredService<TimeProvider>()));
    builder.Services.AddHttpClient(KatalepsisFT.Venues.Rest.BitgetOrderGateway.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(8));
    builder.Services.AddSingleton<KatalepsisFT.Application.Execution.IOrderGateway>(provider =>
        new KatalepsisFT.Venues.Rest.BitgetOrderGateway(provider.GetRequiredService<IHttpClientFactory>().CreateClient(KatalepsisFT.Venues.Rest.BitgetOrderGateway.HttpClientName), provider.GetRequiredService<TimeProvider>()));
    builder.Services.AddHttpClient(KatalepsisFT.Venues.Deribit.DeribitOrderGateway.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(8));
    builder.Services.AddSingleton<KatalepsisFT.Application.Execution.IOrderGateway>(provider =>
        new KatalepsisFT.Venues.Deribit.DeribitOrderGateway(provider.GetRequiredService<IHttpClientFactory>().CreateClient(KatalepsisFT.Venues.Deribit.DeribitOrderGateway.HttpClientName), provider.GetRequiredService<TimeProvider>()));
    builder.Services.AddHttpClient(KatalepsisFT.Venues.Coinbase.CoinbaseOrderGateway.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(8));
    builder.Services.AddSingleton<KatalepsisFT.Application.Execution.IOrderGateway>(provider =>
        new KatalepsisFT.Venues.Coinbase.CoinbaseOrderGateway(provider.GetRequiredService<IHttpClientFactory>().CreateClient(KatalepsisFT.Venues.Coinbase.CoinbaseOrderGateway.HttpClientName), provider.GetRequiredService<TimeProvider>()));
    builder.Services.AddHttpClient(KatalepsisFT.Venues.Mexc.MexcOrderGateway.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(8));
    builder.Services.AddSingleton<KatalepsisFT.Application.Execution.IOrderGateway>(provider =>
        new KatalepsisFT.Venues.Mexc.MexcOrderGateway(provider.GetRequiredService<IHttpClientFactory>().CreateClient(KatalepsisFT.Venues.Mexc.MexcOrderGateway.HttpClientName), provider.GetRequiredService<TimeProvider>()));
    builder.Services.AddHttpClient(KatalepsisFT.Venues.Rest.GateOrderGateway.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(8));
    builder.Services.AddSingleton<KatalepsisFT.Application.Execution.IOrderGateway>(provider =>
        new KatalepsisFT.Venues.Rest.GateOrderGateway(provider.GetRequiredService<IHttpClientFactory>().CreateClient(KatalepsisFT.Venues.Rest.GateOrderGateway.HttpClientName), provider.GetRequiredService<TimeProvider>()));
    builder.Services.AddHttpClient(KatalepsisFT.Venues.Rest.KrakenOrderGateway.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(8));
    builder.Services.AddSingleton<KatalepsisFT.Application.Execution.IOrderGateway>(provider =>
        new KatalepsisFT.Venues.Rest.KrakenOrderGateway(provider.GetRequiredService<IHttpClientFactory>().CreateClient(KatalepsisFT.Venues.Rest.KrakenOrderGateway.HttpClientName), provider.GetRequiredService<TimeProvider>()));
    builder.Services.AddScoped<KatalepsisFT.Application.Execution.LiveExecution>();
    builder.Services.AddHostedService<OrderEngineWorker>();
    builder.Services.AddHostedService<LiveOrdersWorker>();
}

await builder.Build().RunAsync();
return 0;
