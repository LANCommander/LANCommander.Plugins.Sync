using LANCommander.Plugins.Sync.Data;

namespace LANCommander.Plugins.Sync.Syncing;

/// <summary>How the sync that's running is getting on, for the plugin's pages to show.</summary>
public sealed class SyncProgress(Guid remoteId, string remoteName, SyncTrigger trigger)
{
    public Guid RemoteId { get; } = remoteId;

    public string RemoteName { get; } = remoteName;

    public SyncTrigger Trigger { get; } = trigger;

    public DateTime StartedOn { get; } = DateTime.UtcNow;

    public SyncStage Stage { get; private set; } = SyncStage.Connecting;

    /// <summary>Records that need importing.</summary>
    public int Total { get; private set; }

    /// <summary>Records imported or failed so far.</summary>
    public int Done { get; private set; }

    public int Failed { get; private set; }

    /// <summary>The record being imported.</summary>
    public string? Current { get; private set; }

    /// <summary>Bytes of the current record's package downloaded so far.</summary>
    public long CurrentDownloaded { get; private set; }

    /// <summary>The current record's package size, when the remote server says.</summary>
    public long? CurrentLength { get; private set; }

    /// <summary>Raised on every change. Handlers run on the sync's thread and must not block.</summary>
    public event Action? Changed;

    internal void ReadingCatalog() => Set(() => Stage = SyncStage.ReadingCatalog);

    internal void Importing(int total) => Set(() =>
    {
        Stage = SyncStage.Importing;
        Total = total;
    });

    internal void Starting(string name, long? length) => Set(() =>
    {
        Current = name;
        CurrentDownloaded = 0;
        CurrentLength = length;
    });

    internal void Downloaded(long bytes) => Set(() => CurrentDownloaded = bytes);

    internal void Finished(bool succeeded) => Set(() =>
    {
        Done++;

        if (!succeeded)
            Failed++;

        Current = null;
        CurrentDownloaded = 0;
        CurrentLength = null;
    });

    void Set(Action change)
    {
        change();
        Changed?.Invoke();
    }
}

public enum SyncStage
{
    Connecting,
    ReadingCatalog,
    Importing,
}
