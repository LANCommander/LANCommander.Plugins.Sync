using System.Text.Json;
using System.Text.Json.Serialization;
using LANCommander.Plugins.Sync.Remotes;
using LANCommander.SDK.Enums;

namespace LANCommander.Plugins.Sync.Protocol;

/// <summary>
/// What servers running the plugin say to each other, under <c>/api/Plugins/Sync</c>. The server that
/// syncs asks; the remote server answers.
/// </summary>
public static class SyncProtocol
{
    /// <summary>Raised when a change would break a server running an older plugin.</summary>
    public const int Version = 1;

    public const string RoutePrefix = "Sync";

    public const string InfoPath = "Info";
    public const string CollectionsPath = "Collections";
    public const string CatalogPath = "Catalog";
    public const string ExportPath = "Export";

    public const string PackageContentType = "application/vnd.lancommander.lcx";

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Answers <c>GET Info</c>: who the remote server is.</summary>
/// <param name="InstanceId">The remote server's sync identity.</param>
/// <param name="ProtocolVersion">The <see cref="SyncProtocol.Version"/> it speaks.</param>
/// <param name="PluginVersion">The version of the plugin it runs.</param>
public sealed record RemoteInfo(Guid InstanceId, int ProtocolVersion, string PluginVersion);

/// <summary>One of the remote server's collections, to filter games by.</summary>
public sealed record CollectionInfo(Guid Id, string Name);

/// <summary>Asks for the records to sync, in <c>POST Catalog</c>.</summary>
/// <param name="RequesterId">The asking server's instance id.</param>
public sealed record CatalogRequest(
    Guid RequesterId,
    GameSyncMode Games,
    IReadOnlyList<Guid> CollectionIds,
    DependencySyncMode Redistributables,
    DependencySyncMode Tools);

/// <summary>Answers <c>POST Catalog</c>.</summary>
public sealed record CatalogResponse(IReadOnlyList<CatalogEntry> Entries);

/// <summary>A record the remote server offers.</summary>
/// <param name="Type">Game, redistributable or tool.</param>
/// <param name="Id">The record's id, kept by the import.</param>
/// <param name="Name">Its title or name.</param>
/// <param name="BaseGameId">For an add-on, the game it adds to, which is synced first.</param>
/// <param name="Fingerprint">Changes whenever the record or anything a package of it carries changes.</param>
/// <param name="OriginId">
/// The instance id of the server the record's current state was made on, when that isn't the remote
/// server itself because the remote synced it from elsewhere and hasn't changed it since.
/// </param>
/// <param name="Archives">The record's archives, so the asking server can leave out those it holds.</param>
public sealed record CatalogEntry(
    ImportExportRecordType Type,
    Guid Id,
    string Name,
    Guid? BaseGameId,
    string Fingerprint,
    Guid? OriginId,
    IReadOnlyList<CatalogArchive> Archives);

/// <summary>An archive of a record, with the size of its file.</summary>
public sealed record CatalogArchive(Guid Id, long Size);

/// <summary>Asks for a package of a record, in <c>POST Export</c>.</summary>
/// <param name="ExcludedItemIds">Child records to leave out, such as archives the asking server holds.</param>
/// <param name="IncludeKeys">Whether a game's keys are packed.</param>
public sealed record ExportRequest(
    ImportExportRecordType Type,
    Guid Id,
    IReadOnlyList<Guid> ExcludedItemIds,
    bool IncludeKeys);
