using LANCommander.Server.Plugins.Data;
using Microsoft.EntityFrameworkCore;

namespace LANCommander.Plugins.Sync.Data;

/// <summary>
/// What the plugin remembers about each sync: the records it has brought over from each remote server,
/// and the runs that did it. Its tables are Plugin_Sync_Records and Plugin_Sync_Runs.
/// </summary>
public sealed class SyncContext(DbContextOptions options) : PluginDbContext(options)
{
    /// <summary>The <c>DataId</c> the plugin declares; its tables are named after it.</summary>
    public const string PluginDataId = "Sync";

    public DbSet<SyncedRecord> Records => Set<SyncedRecord>();

    public DbSet<SyncRun> Runs => Set<SyncRun>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SyncedRecord>(record =>
        {
            record.Property(r => r.Name).HasMaxLength(256);
            record.Property(r => r.RemoteFingerprint).HasMaxLength(64);
            record.Property(r => r.LocalFingerprint).HasMaxLength(64);
            record.Property(r => r.LastError).HasMaxLength(2000);

            record.HasIndex(r => new { r.RemoteId, r.RecordType, r.RecordId }).IsUnique();
            // For telling a remote server which of the records it asks about came from it
            record.HasIndex(r => new { r.RecordType, r.RecordId });
        });

        modelBuilder.Entity<SyncRun>(run =>
        {
            run.Property(r => r.Error).HasMaxLength(2000);

            run.HasIndex(r => new { r.RemoteId, r.StartedOn });
        });
    }
}
