using LANCommander.SDK.Enums;

namespace LANCommander.Plugins.Sync.Data;

/// <summary>A game, redistributable or tool this server has brought over, or tried to, from a remote server.</summary>
public sealed class SyncedRecord
{
    public Guid Id { get; set; }

    /// <summary>The remote server it came from, as configured in the plugin's settings.</summary>
    public Guid RemoteId { get; set; }

    public ImportExportRecordType RecordType { get; set; }

    /// <summary>The record's id, which is the same on both servers.</summary>
    public Guid RecordId { get; set; }

    /// <summary>Its title or name when it was last seen, for display.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The remote server's fingerprint of the record when it was last imported. A different one means
    /// it has changed there since. Null until an import succeeds.
    /// </summary>
    public string? RemoteFingerprint { get; set; }

    /// <summary>
    /// This server's fingerprint of the record just after it was imported. While the record still
    /// matches it, it hasn't been changed here, so its current state came from <see cref="OriginId"/>.
    /// </summary>
    public string? LocalFingerprint { get; set; }

    /// <summary>
    /// The instance id of the server the record's state was first made on. A server never takes a
    /// record back from a remote while it originated with itself, so two servers syncing from each
    /// other don't pass the same change back and forth.
    /// </summary>
    public Guid? OriginId { get; set; }

    /// <summary>When the record was last imported.</summary>
    public DateTime? SyncedOn { get; set; }

    /// <summary>When an import of the record was last tried.</summary>
    public DateTime? LastAttemptOn { get; set; }

    /// <summary>Why the last attempt failed, or null when it succeeded.</summary>
    public string? LastError { get; set; }
}
