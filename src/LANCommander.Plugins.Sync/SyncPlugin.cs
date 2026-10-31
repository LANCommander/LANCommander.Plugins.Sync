using System.Reflection;
using LANCommander.Plugins.Sync.Data;
using LANCommander.Plugins.Sync.Remotes;
using LANCommander.Plugins.Sync.Source;
using LANCommander.Plugins.Sync.Syncing;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using LANCommander.Server.Plugins.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

[assembly: LANCommanderPlugin(
    typeof(LANCommander.Plugins.Sync.SyncPlugin),
    Id = LANCommander.Plugins.Sync.SyncPlugin.PluginId,
    // IPackageExporter and IRecurringJobScheduler first ship in 2.2.0-beta.1
    MinHostVersion = "2.2.0-beta.1",
    Hosts = PluginHost.Server,
    DataId = SyncContext.PluginDataId,
    MigrationAssemblies =
    [
        "LANCommander.Plugins.Sync.Migrations.Sqlite",
        "LANCommander.Plugins.Sync.Migrations.MySql",
        "LANCommander.Plugins.Sync.Migrations.PostgreSql",
    ])]

namespace LANCommander.Plugins.Sync;

/// <summary>
/// Syncs games, redistributables and tools between LANCommander servers. Each server lists the remote
/// servers it brings records over from, under Settings → Server Sync, and serves its own records to
/// the servers that list it.
/// </summary>
public sealed class SyncPlugin : IPlugin
{
    public const string PluginId = "lancommander.sync";

    public static string PluginVersion => typeof(SyncPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public string Id => PluginId;
    public string Name => "Server Sync";
    public string Version => PluginVersion;
    public string Author => "LANCommander";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IServerRouteAssemblyExtension>(new SyncRouteAssembly());
        services.AddSingleton<IServerNavigationExtension, SyncNavigation>();
        services.AddSingleton<IServerEndpointExtension, SyncEndpoints>();

        services.AddPluginDbContext<SyncContext>();

        // IPackageExporter, IPackageImporter, IRecurringJobScheduler and the record providers come from the server
        AddSyncServices(services);

        services.AddHostedService(provider => provider.GetRequiredService<SyncScheduler>());
    }

    public Task InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        var remotes = context.Services.GetRequiredService<RemoteServers>();

        context.Logger.LogInformation(
            "Server Sync is instance {InstanceId} and syncs from {Count} remote server(s)",
            remotes.InstanceId,
            remotes.All.Count);

        // Here rather than at startup: the plugin is only initialized once its data is ready
        context.Services.GetRequiredService<SyncJobs>().Start();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers everything the plugin needs that doesn't come from the host, so the tests can build
    /// the same container.
    /// </summary>
    internal static void AddSyncServices(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<RemoteServers>();
        services.AddSingleton<SyncScheduler>();
        services.AddSingleton<SyncJobs>();
        services.AddScoped<IRecurringJobHandler, SyncJobHandler>();
        services.AddScoped<CatalogService>();
        services.AddScoped<SyncRunner>();
        services.AddScoped<SyncHistory>();
        services.AddScoped<RemoteClient>();

        services.AddHttpClient(RemoteClient.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"LANCommander-Sync/{PluginVersion}");
        });
    }

    private sealed class SyncRouteAssembly : IServerRouteAssemblyExtension
    {
        public Assembly Assembly => typeof(SyncPlugin).Assembly;
    }

    private sealed class SyncNavigation : IServerNavigationExtension
    {
        public string Id => $"{PluginId}.remotes";
        public string Label => "Server Sync";
        public string Href => SyncRoutes.Remotes;
        public string? Icon => "ArrowsClockwise";
        public string? Section => ServerNavigationSection.Settings;
        public string? After => "/Settings/Plugins";
    }
}

/// <summary>The plugin's pages.</summary>
public static class SyncRoutes
{
    public const string Remotes = "/Plugins/Sync";

    public static string Remote(Guid id) => $"/Plugins/Sync/{id}";
}
