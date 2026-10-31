using LANCommander.Plugins.Sync.Syncing;
using Microsoft.AspNetCore.Components;

namespace LANCommander.Plugins.Sync.Components;

/// <summary>
/// A page that shows the scheduler's syncs as they happen. Progress arrives many times a second while
/// a package downloads, so the page redraws at most twice a second, and reloads its data when a sync
/// finishes.
/// </summary>
public abstract class SyncPageBase : ComponentBase, IDisposable
{
    static readonly TimeSpan RedrawInterval = TimeSpan.FromMilliseconds(500);

    [Inject] protected SyncScheduler Scheduler { get; set; } = default!;

    readonly CancellationTokenSource _disposed = new();
    volatile bool _dirty;
    Guid? _running;

    protected override void OnInitialized()
    {
        _running = Scheduler.Running?.RemoteId;
        Scheduler.Changed += OnSchedulerChanged;

        _ = RedrawAsync(_disposed.Token);
    }

    /// <summary>Reloads what the page shows once a sync has finished.</summary>
    protected abstract Task OnSyncFinishedAsync(Guid remoteId);

    void OnSchedulerChanged() => _dirty = true;

    async Task RedrawAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RedrawInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!_dirty)
                    continue;

                _dirty = false;

                var running = Scheduler.Running?.RemoteId;

                if (_running is { } finished && finished != running)
                    await InvokeAsync(() => OnSyncFinishedAsync(finished));

                _running = running;

                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // The circuit went away between ticks
        }
    }

    public void Dispose()
    {
        Scheduler.Changed -= OnSchedulerChanged;

        _disposed.Cancel();
        _disposed.Dispose();
    }
}
