using System.Reflection;
using System.Runtime.Loader;
using LANCommander.Plugins.Sync.Data;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using LANCommander.Server.Plugins.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LANCommander.Plugins.Sync.Tests;

/// <summary>
/// Loads the built drop-in folder the way the server does, to prove it carries what it needs and
/// nothing that would load a second copy of a host type.
/// </summary>
public sealed class PluginLoadingTests : IDisposable
{
    private const string PluginAssembly = "LANCommander.Plugins.Sync";

    private static readonly string[] MigrationAssemblies =
    [
        "LANCommander.Plugins.Sync.Migrations.MySql",
        "LANCommander.Plugins.Sync.Migrations.PostgreSql",
        "LANCommander.Plugins.Sync.Migrations.Sqlite",
    ];

    private readonly ScratchDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void TheDropInFolderHoldsThePluginItsContextAndItsMigrations()
    {
        var assemblies = System.IO.Directory.GetFiles(PluginOutputDirectory(), "*.dll").Select(Path.GetFileNameWithoutExtension).Order();

        Assert.Equal([PluginAssembly, "LANCommander.Plugins.Sync.Data", .. MigrationAssemblies], assemblies);
        Assert.Empty(System.IO.Directory.GetDirectories(PluginOutputDirectory()));
    }

    [Fact]
    public void TheServerLoadsThePluginAndFindsWhatItAdds()
    {
        var services = new ServiceCollection();
        var loader = new PluginLoaderService();

        loader.DiscoverAndConfigure(services, PluginHost.Server, Deploy(), "2.2.0", NullLogger.Instance);

        Assert.Equal(SyncPlugin.PluginId, Assert.Single(loader.LoadedPlugins).Id);

        // Registered as the host's interface types, so the server finds them
        Assert.Contains(services, service => service.ServiceType == typeof(IServerEndpointExtension));
        Assert.Contains(services, service => service.ServiceType == typeof(IHostedService));
        Assert.Contains(services, service => service.ServiceType == typeof(IRecurringJobHandler));
        Assert.Equal("SyncContext", Assert.Single(services, service => service.ServiceType == typeof(PluginDbContextRegistration))
            .ImplementationInstance is PluginDbContextRegistration registration ? registration.ContextType.Name : null);

        var navigation = services
            .Where(service => service.ServiceType == typeof(IServerNavigationExtension))
            .Select(service => (IServerNavigationExtension)Activator.CreateInstance(service.ImplementationType!, nonPublic: true)!)
            .Single();

        Assert.Equal(SyncRoutes.Remotes, navigation.Href);
        Assert.Equal(ServerNavigationSection.Main, navigation.Section);

        var routes = (IServerRouteAssemblyExtension)services
            .Single(service => service.ServiceType == typeof(IServerRouteAssemblyExtension)).ImplementationInstance!;

        var pluginContext = AssemblyLoadContext.GetLoadContext(routes.Assembly);

        Assert.IsType<PluginLoadContext>(pluginContext);

        // Every host assembly the plugin references resolves to the host's copy
        foreach (var reference in routes.Assembly.GetReferencedAssemblies().Where(IsHostAssembly))
        {
            var loaded = pluginContext!.LoadFromAssemblyName(reference);

            Assert.True(
                AssemblyLoadContext.GetLoadContext(loaded) == AssemblyLoadContext.Default,
                $"{reference.Name} loaded into {AssemblyLoadContext.GetLoadContext(loaded)?.Name}");
        }
    }

    [Fact]
    public void TheAttributeNamesTheDataAndAMigrationsAssemblyForEveryEngine()
    {
        var attribute = typeof(SyncPlugin).Assembly.GetCustomAttribute<LANCommanderPluginAttribute>()!;

        Assert.Equal(SyncContext.PluginDataId, attribute.DataId);
        Assert.Equal(MigrationAssemblies, attribute.MigrationAssemblies!.Order());

        var engines = MigrationAssemblies
            .Select(name => Assembly.LoadFrom(Path.Combine(PluginOutputDirectory(), $"{name}.dll")))
            .SelectMany(assembly => assembly.GetCustomAttributes<PluginMigrationsAttribute>())
            .ToList();

        Assert.All(engines, migrations => Assert.Equal(typeof(SyncContext).FullName, migrations.ContextType.FullName));
        Assert.Equal([DatabaseEngine.Sqlite, DatabaseEngine.MySql, DatabaseEngine.PostgreSql], engines.Select(m => m.Engine).Order());
    }

    [Theory]
    [InlineData("2.2.0-alpha.4", false)]
    [InlineData("2.2.0-beta.1", true)]
    [InlineData("2.2.0", true)]
    public void TheServerSkipsThePluginWhenItPredatesTheExporter(string hostVersion, bool loads)
    {
        var loader = new PluginLoaderService();

        loader.DiscoverAndConfigure(new ServiceCollection(), PluginHost.Server, Deploy(), hostVersion, NullLogger.Instance);

        Assert.Equal(loads, loader.LoadedPlugins.Count == 1);
    }

    /// <summary>The plugin's own assemblies are its to load; everything else LANCommander is the host's.</summary>
    private static bool IsHostAssembly(AssemblyName reference) =>
        reference.Name!.StartsWith("LANCommander.", StringComparison.Ordinal)
        && !reference.Name.StartsWith("LANCommander.Plugins.Sync", StringComparison.Ordinal);

    private string Deploy()
    {
        var root = Path.Combine(_directory.Path, "Plugins");
        var folder = Path.Combine(root, PluginAssembly);

        System.IO.Directory.CreateDirectory(folder);

        foreach (var file in System.IO.Directory.GetFiles(PluginOutputDirectory()))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));

        return root;
    }

    private static string PluginOutputDirectory()
        => typeof(PluginLoadingTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PluginOutputDirectory").Value!;
}

internal sealed class ScratchDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sync-plugin-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows keeps the plugin's assemblies locked while they're loaded
        }
    }
}
