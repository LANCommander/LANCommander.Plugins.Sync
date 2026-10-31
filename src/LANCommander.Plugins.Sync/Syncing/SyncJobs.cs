using LANCommander.Plugins.Sync.Data;
using LANCommander.Plugins.Sync.Remotes;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Sync.Syncing;

/// <summary>
/// Keeps a recurring job on the server's background job processor for every remote server that syncs
/// automatically, on its schedule. Each job asks <see cref="SyncScheduler"/> for a sync and waits for it,
/// so the dashboard shows how long it took.
/// </summary>
/// <remarks>The server forgets recurring jobs when it stops, so they're added again on every start.</remarks>
public sealed class SyncJobs(RemoteServers remotes, IRecurringJobScheduler jobs, ILogger<SyncJobs> logger) : IDisposable
{
    public const string HandlerName = $"{SyncPlugin.PluginId}.remote";

    private readonly Lock _lock = new();
    private HashSet<Guid> _scheduled = [];
    private bool _started;

    public static string JobId(Guid remoteId) => $"{SyncPlugin.PluginId}.{remoteId:N}";

    /// <summary>Schedules every remote server that syncs automatically, and follows changes to them.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started)
                return;

            _started = true;
        }

        remotes.Changed += Refresh;
        Refresh();
    }

    /// <summary>When the remote server's next scheduled sync is, in UTC, if it has one.</summary>
    public DateTime? NextSync(Guid remoteId) => jobs.Get(JobId(remoteId))?.NextExecution;

    void Refresh()
    {
        lock (_lock)
        {
            var wanted = remotes.All.Where(r => r.Enabled).ToList();

            foreach (var remote in wanted)
            {
                try
                {
                    jobs.AddOrUpdate(JobId(remote.Id), HandlerName, remote.Id.ToString(), remote.Schedule);
                }
                catch (ArgumentException ex)
                {
                    // Saved before the schedule was checked, or edited by hand; it can still be synced by hand
                    logger.LogError(ex, "{Remote} can't be synced on its schedule \"{Schedule}\"", remote.Name, remote.Schedule);
                }
            }

            var wantedIds = wanted.Select(r => r.Id).ToHashSet();

            foreach (var removed in _scheduled.Except(wantedIds))
                jobs.Remove(JobId(removed));

            _scheduled = wantedIds;
        }
    }

    public void Dispose() => remotes.Changed -= Refresh;
}

/// <summary>Runs a remote server's scheduled sync when the server's background jobs say it's time.</summary>
public sealed class SyncJobHandler(SyncScheduler scheduler, RemoteServers remotes) : IRecurringJobHandler
{
    public string Name => SyncJobs.HandlerName;

    public Task ExecuteAsync(string argument, CancellationToken cancellationToken)
    {
        // Removed or switched off since the job was added; the next refresh removes the job
        if (!Guid.TryParse(argument, out var remoteId) || remotes.Find(remoteId) is not { Enabled: true })
            return Task.CompletedTask;

        return scheduler.RequestAndWaitAsync(remoteId, SyncTrigger.Scheduled, cancellationToken);
    }
}
