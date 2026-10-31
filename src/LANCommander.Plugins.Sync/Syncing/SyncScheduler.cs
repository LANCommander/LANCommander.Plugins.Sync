using System.Collections.Concurrent;
using System.Threading.Channels;
using LANCommander.Plugins.Sync.Data;
using LANCommander.Plugins.Sync.Remotes;
using LANCommander.Server.Plugins.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Sync.Syncing;

/// <summary>
/// Runs the syncs asked for, one at a time: imports are heavy, and two at once could both bring over
/// the same record. Scheduled syncs are asked for by the server's background jobs (see
/// <see cref="SyncJobs"/>), others by an administrator.
/// </summary>
/// <remarks>
/// Nothing may escape <see cref="ExecuteAsync"/>: the host stops itself when a background service
/// faults, so a failed sync is recorded against its run and the queue carries on.
/// </remarks>
public sealed class SyncScheduler(
    RemoteServers remotes,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<SyncScheduler> logger) : BackgroundService
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, SyncTrigger> _pending = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _completions = new();
    private CancellationTokenSource? _current;
    private bool _recovered;

    /// <summary>The sync that's running, if any.</summary>
    public SyncProgress? Running { get; private set; }

    /// <summary>Remote servers waiting their turn.</summary>
    public IReadOnlyCollection<Guid> Pending => _pending.Keys.ToList();

    /// <summary>Raised when a sync is queued, starts, progresses or finishes. Handlers must not block.</summary>
    public event Action? Changed;

    /// <summary>Queues a sync of the remote server, unless one is queued or running already.</summary>
    /// <returns>Whether it was queued.</returns>
    public bool Request(Guid remoteId, SyncTrigger trigger = SyncTrigger.Manual)
    {
        if (Running?.RemoteId == remoteId || !_pending.TryAdd(remoteId, trigger))
            return false;

        _completions.GetOrAdd(remoteId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _queue.Writer.TryWrite(remoteId);
        Changed?.Invoke();

        return true;
    }

    /// <summary>
    /// Queues a sync of the remote server, unless one is queued or running already, and waits for that
    /// sync to finish, however it ends.
    /// </summary>
    public async Task RequestAndWaitAsync(Guid remoteId, SyncTrigger trigger, CancellationToken cancellationToken)
    {
        Request(remoteId, trigger);

        if (_completions.TryGetValue(remoteId, out var completion))
            await completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Stops the remote server's sync, whether it's running or waiting.</summary>
    public void Cancel(Guid remoteId)
    {
        if (_pending.TryRemove(remoteId, out _))
        {
            Complete(remoteId);
            Changed?.Invoke();
        }

        if (Running?.RemoteId == remoteId)
            _current?.Cancel();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var remoteId in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await RecoverAsync(stoppingToken);
                await RunAsync(remoteId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Nothing will run what's left; let whatever waits on it go
            foreach (var remoteId in _completions.Keys)
                Complete(remoteId);
        }
    }

    /// <summary>Marks the runs a restart cut short, once the plugin's data can be reached.</summary>
    async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (_recovered)
            return;

        try
        {
            await using var scope = scopes.CreateAsyncScope();

            var data = scope.ServiceProvider.GetRequiredService<SyncContext>();
            var interrupted = await data.Runs.Where(r => r.Status == SyncRunStatus.Running).ToListAsync(cancellationToken);

            foreach (var run in interrupted)
            {
                run.Status = SyncRunStatus.Failed;
                run.FinishedOn ??= time.GetUtcNow().UtcDateTime;
                run.Error = "The server stopped before this sync finished.";
            }

            await data.SaveChangesAsync(cancellationToken);

            _recovered = true;
        }
        catch (PluginDataUnavailableException ex)
        {
            logger.LogDebug(ex, "The Sync plugin's data isn't ready yet");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not tidy up syncs a restart interrupted");
        }
    }

    async Task RunAsync(Guid remoteId, CancellationToken stoppingToken)
    {
        // Cancelled, or asked for twice, while it waited
        if (!_pending.TryRemove(remoteId, out var trigger))
            return;

        var remote = remotes.Find(remoteId);

        if (remote == null)
        {
            Complete(remoteId);
            Changed?.Invoke();
            return;
        }

        using var current = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        var progress = new SyncProgress(remote.Id, remote.Name, trigger);

        progress.Changed += OnProgress;

        _current = current;
        Running = progress;
        Changed?.Invoke();

        try
        {
            await using var scope = scopes.CreateAsyncScope();

            await scope.ServiceProvider.GetRequiredService<SyncRunner>().RunAsync(remote, progress, current.Token);
        }
        catch (Exception ex)
        {
            // The runner records its own failures; this is one it couldn't, e.g. the plugin's data going away
            logger.LogError(ex, "Syncing from {Remote} failed", remote.Name);
        }
        finally
        {
            progress.Changed -= OnProgress;

            Running = null;
            _current = null;
            Complete(remoteId);
            Changed?.Invoke();
        }
    }

    void Complete(Guid remoteId)
    {
        if (_completions.TryRemove(remoteId, out var completion))
            completion.TrySetResult();
    }

    void OnProgress() => Changed?.Invoke();
}
