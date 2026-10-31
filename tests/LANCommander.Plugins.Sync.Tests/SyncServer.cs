using System.Collections.Concurrent;
using LANCommander.Plugins.Sync.Data;
using LANCommander.Plugins.Sync.Remotes;
using LANCommander.Plugins.Sync.Source;
using LANCommander.Plugins.Sync.Syncing;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using LANCommander.Server.Plugins.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Sync.Tests;

/// <summary>The servers in a test, reachable from each other by name.</summary>
internal sealed class Network : IHttpClientFactory
{
    private readonly ConcurrentDictionary<string, TestServer> _servers = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string host, TestServer server) => _servers[host] = server;

    public HttpClient CreateClient(string name) => new(new RoutingHandler(this)) { Timeout = Timeout.InfiniteTimeSpan };

    sealed class RoutingHandler(Network network) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!network._servers.TryGetValue(request.RequestUri!.Host, out var server))
                throw new HttpRequestException($"No such host: {request.RequestUri.Host}");

            using var invoker = new HttpMessageInvoker(server.CreateHandler(), disposeHandler: true);

            return await invoker.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>
/// One LANCommander server running the plugin: its library, the plugin's database, its settings and
/// its Sync endpoints, served in memory. It both serves its records and syncs from others.
/// </summary>
internal sealed class SyncServer : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly WebApplication _app;

    public string Name { get; }
    public string Address => $"http://{Name}.test";
    public FakeLibrary Library { get; } = new();
    public MemorySettings<SyncSettings> Settings { get; } = new();
    public IServiceProvider Services => _app.Services;
    public RemoteServers Remotes => Services.GetRequiredService<RemoteServers>();

    private SyncServer(string name, SqliteConnection connection, WebApplication app, FakeLibrary library, MemorySettings<SyncSettings> settings)
    {
        Name = name;
        _connection = connection;
        _app = app;
        Library = library;
        Settings = settings;
    }

    public static async Task<SyncServer> StartAsync(string name, Network network)
    {
        var connection = new SqliteConnection("Data Source=:memory:");

        await connection.OpenAsync();

        var library = new FakeLibrary();
        var settings = new MemorySettings<SyncSettings>();

        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        AddServices(builder.Services, library, settings, connection, network);

        var app = builder.Build();

        new SyncEndpoints().MapEndpoints(app.MapGroup($"/api/Plugins/{Protocol.SyncProtocol.RoutePrefix}"));

        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<SyncContext>().Database.EnsureCreatedAsync();

        await app.StartAsync();

        var server = new SyncServer(name, connection, app, library, settings);

        network.Register($"{name}.test", app.GetTestServer());

        return server;
    }

    /// <summary>
    /// What a server running the plugin has: the host's contracts, backed by the library, the plugin's
    /// context on the server's connection, and the plugin's own services.
    /// </summary>
    public static void AddServices(IServiceCollection services, FakeLibrary library, MemorySettings<SyncSettings> settings, SqliteConnection connection, Network network)
    {
        services.AddSingleton<IGameProvider>(library);
        services.AddSingleton<IRedistributableProvider>(library);
        services.AddSingleton<IToolProvider>(library);
        services.AddSingleton<ITaxonomyProvider>(library);
        services.AddSingleton<IArchiveProvider>(library);
        services.AddSingleton<IPackageExporter>(library);
        services.AddSingleton<IPackageImporter>(library);
        services.AddSingleton<IStorageLocationProvider>(new FakeStorageLocations());
        services.AddSingleton<FakeJobScheduler>();
        services.AddSingleton<IRecurringJobScheduler>(provider => provider.GetRequiredService<FakeJobScheduler>());
        services.AddSingleton<IPluginSettings<SyncSettings>>(settings);

        // As the host makes it: named after the plugin, on the server's connection
        services.AddDbContextFactory<SyncContext>(options => options
            .UseSqlite(connection)
            .UsePluginIdentity(SyncContext.PluginDataId));

        SyncPlugin.AddSyncServices(services);

        services.AddSingleton<IHttpClientFactory>(network);
    }

    /// <summary>Adds a remote server to sync from, by default everything it has.</summary>
    public RemoteServer AddRemote(SyncServer other, Action<RemoteServer>? configure = null)
    {
        var remote = new RemoteServer
        {
            Name = other.Name,
            Address = other.Address,
            AccessToken = "lc_pat_test",
            Games = GameSyncMode.All,
            Redistributables = DependencySyncMode.All,
            Tools = DependencySyncMode.All,
        };

        configure?.Invoke(remote);

        Remotes.Save(remote);

        return remote;
    }

    public async Task<SyncRun> SyncFromAsync(RemoteServer remote, CancellationToken cancellationToken = default)
    {
        await using var scope = Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<SyncRunner>().RunAsync(remote, new SyncProgress(remote.Id, remote.Name, SyncTrigger.Manual), cancellationToken);
    }

    public async Task<List<SyncedRecord>> SyncedRecordsAsync()
    {
        await using var scope = Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<SyncContext>().Records.AsNoTracking().ToListAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

/// <summary>
/// The server's recurring jobs, kept in memory and run when a test says so rather than on their
/// schedules, the way the server's runner does: a handler found by name, in a scope of its own.
/// </summary>
internal sealed class FakeJobScheduler(IServiceScopeFactory scopes) : IRecurringJobScheduler
{
    public ConcurrentDictionary<string, (string Handler, string Argument, string Cron)> Jobs { get; } = new();

    public void AddOrUpdate(string jobId, string handler, string argument, string cronExpression, TimeZoneInfo? timeZone = null)
    {
        if (!IsValidCronExpression(cronExpression))
            throw new ArgumentException($"Not a cron expression: {cronExpression}", nameof(cronExpression));

        Jobs[jobId] = (handler, argument, cronExpression);
    }

    public void Remove(string jobId) => Jobs.TryRemove(jobId, out _);

    public void Trigger(string jobId) => throw new NotSupportedException("Use RunAsync");

    public RecurringJobInfo? Get(string jobId) =>
        Jobs.TryGetValue(jobId, out var job) ? new RecurringJobInfo(jobId, job.Cron, new DateTime(2030, 1, 1, 2, 0, 0, DateTimeKind.Utc), null) : null;

    /// <summary>Five or six fields of digits and cron punctuation; the server checks properly.</summary>
    public bool IsValidCronExpression(string cronExpression) =>
        cronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: 5 or 6 } fields
        && fields.All(field => field.All(c => char.IsDigit(c) || c is '*' or '/' or ',' or '-'));

    /// <summary>Runs the job as the server would when its time comes.</summary>
    public async Task RunAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var job = Jobs[jobId];

        await using var scope = scopes.CreateAsyncScope();

        await scope.ServiceProvider.GetServices<IRecurringJobHandler>().Single(h => h.Name == job.Handler).ExecuteAsync(job.Argument, cancellationToken);
    }
}

/// <summary>One archive storage location, the default.</summary>
internal sealed class FakeStorageLocations : IStorageLocationProvider
{
    public static readonly Guid DefaultId = Guid.NewGuid();

    public Task<IReadOnlyList<StorageLocationInfo>> GetAsync(SDK.Enums.StorageLocationType type, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StorageLocationInfo>>([new StorageLocationInfo(DefaultId, "Uploads", type, true)]);
}

/// <summary>Plugin settings kept in memory, as the host keeps them in a file.</summary>
internal sealed class MemorySettings<T> : IPluginSettings<T> where T : class, new()
{
    public T Current { get; } = new();

    public event Action? Changed;

    public void Update(Action<T> mutator)
    {
        mutator(Current);
        Changed?.Invoke();
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
