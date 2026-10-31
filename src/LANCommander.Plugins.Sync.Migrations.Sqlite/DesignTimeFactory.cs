using LANCommander.Plugins.Sync.Data;
using LANCommander.Server.Plugins.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

[assembly: PluginMigrations(typeof(SyncContext), DatabaseEngine.Sqlite)]

namespace LANCommander.Plugins.Sync.Migrations.Sqlite;

/// <summary>Builds the context for <c>dotnet ef</c>, named and with the history table the host uses.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<SyncContext>
{
    public SyncContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder();

        // The migrations assembly by name: EF's design-time tooling reads the name, not an Assembly
        builder.UseSqlite("Data Source=:memory:", options => options
            .MigrationsAssembly(typeof(DesignTimeFactory).Assembly.GetName().Name)
            .MigrationsHistoryTable(PluginDataIds.HistoryTable(SyncContext.PluginDataId)));

        return new SyncContext(builder.UsePluginIdentity(SyncContext.PluginDataId).Options);
    }
}
