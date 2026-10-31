using LANCommander.Plugins.Sync.Data;
using Microsoft.EntityFrameworkCore;

namespace LANCommander.Plugins.Sync.Syncing;

/// <summary>Reads what past syncs did, for the plugin's pages.</summary>
/// <remarks>A context per call, since a page lives as long as its circuit.</remarks>
public sealed class SyncHistory(IDbContextFactory<SyncContext> contexts)
{
    /// <summary>Each remote server's most recent run.</summary>
    public async Task<IReadOnlyDictionary<Guid, SyncRun>> GetLatestRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var data = await contexts.CreateDbContextAsync(cancellationToken);

        var runs = await data.Runs
            .AsNoTracking()
            .Where(r => r.StartedOn == data.Runs.Where(o => o.RemoteId == r.RemoteId).Max(o => o.StartedOn))
            .ToListAsync(cancellationToken);

        return runs
            .GroupBy(r => r.RemoteId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>A remote server's runs, newest first.</summary>
    public async Task<IReadOnlyList<SyncRun>> GetRunsAsync(Guid remoteId, int count = 50, CancellationToken cancellationToken = default)
    {
        await using var data = await contexts.CreateDbContextAsync(cancellationToken);

        return await data.Runs
            .AsNoTracking()
            .Where(r => r.RemoteId == remoteId)
            .OrderByDescending(r => r.StartedOn)
            .Take(count)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The records brought over, or tried, from a remote server, failures first.</summary>
    public async Task<IReadOnlyList<SyncedRecord>> GetRecordsAsync(Guid remoteId, CancellationToken cancellationToken = default)
    {
        await using var data = await contexts.CreateDbContextAsync(cancellationToken);

        var records = await data.Records
            .AsNoTracking()
            .Where(r => r.RemoteId == remoteId)
            .ToListAsync(cancellationToken);

        return records
            .OrderBy(r => r.LastError == null)
            .ThenBy(r => r.RecordType)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Forgets what was synced from a remote server, so its next sync imports everything again, and
    /// its run history.
    /// </summary>
    public async Task ForgetAsync(Guid remoteId, CancellationToken cancellationToken = default)
    {
        await using var data = await contexts.CreateDbContextAsync(cancellationToken);

        await data.Records.Where(r => r.RemoteId == remoteId).ExecuteDeleteAsync(cancellationToken);
        await data.Runs.Where(r => r.RemoteId == remoteId && r.Status != SyncRunStatus.Running).ExecuteDeleteAsync(cancellationToken);
    }
}
