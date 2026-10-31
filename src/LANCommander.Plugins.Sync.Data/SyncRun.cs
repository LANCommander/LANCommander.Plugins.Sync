namespace LANCommander.Plugins.Sync.Data;

/// <summary>One sync from a remote server.</summary>
public sealed class SyncRun
{
    public Guid Id { get; set; }

    public Guid RemoteId { get; set; }

    public SyncTrigger Trigger { get; set; }

    public SyncRunStatus Status { get; set; }

    public DateTime StartedOn { get; set; }

    public DateTime? FinishedOn { get; set; }

    /// <summary>Records the remote server offered that the filters let through.</summary>
    public int Checked { get; set; }

    /// <summary>Records that had changed since they were last imported, or were new.</summary>
    public int Changed { get; set; }

    public int Imported { get; set; }

    public int Failed { get; set; }

    /// <summary>Bytes of packages downloaded.</summary>
    public long Downloaded { get; set; }

    /// <summary>Why the run stopped, when it couldn't finish.</summary>
    public string? Error { get; set; }
}

public enum SyncTrigger
{
    Scheduled,
    Manual,
}

public enum SyncRunStatus
{
    Running,
    /// <summary>Every changed record was imported.</summary>
    Succeeded,
    /// <summary>The run finished, but some records failed to import.</summary>
    CompletedWithErrors,
    /// <summary>The run stopped early, e.g. because the remote server couldn't be reached.</summary>
    Failed,
    Cancelled,
}
