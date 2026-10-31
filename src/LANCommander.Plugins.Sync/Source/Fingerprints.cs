using System.Security.Cryptography;
using System.Text;
using LANCommander.SDK.Enums;
using LANCommander.Server.Plugins;

namespace LANCommander.Plugins.Sync.Source;

/// <summary>
/// Sums up a record and the child records a package of it carries, so either server can tell whether
/// it has changed without exporting it.
/// </summary>
/// <remarks>
/// A fingerprint is only ever compared with another from the same server, so it can rest on that
/// server's timestamps. Saves and play sessions belong to players and never travel. Keys count by id
/// alone: adding or removing one is a change, a player claiming one isn't.
/// </remarks>
public static class Fingerprints
{
    /// <summary>Child records that are never synced.</summary>
    public static IReadOnlySet<ImportExportRecordType> NeverSynced { get; } = new HashSet<ImportExportRecordType>
    {
        ImportExportRecordType.Save,
        ImportExportRecordType.PlaySession,
    };

    public static string Compute(PackageContents contents)
    {
        var lines = contents.Items
            .Where(item => !NeverSynced.Contains(item.Type))
            .Select(item => item.Type == ImportExportRecordType.Key
                ? $"{item.Type}:{item.Id:N}"
                : $"{item.Type}:{item.Id:N}:{item.UpdatedOn?.Ticks ?? 0}:{item.Size}")
            .Order(StringComparer.Ordinal)
            .Prepend($"{contents.RecordType}:{contents.RecordId:N}:{contents.UpdatedOn.Ticks}");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)));

        return Convert.ToHexStringLower(hash);
    }

    /// <summary>The child records of a package to leave out, whatever the asking server already has.</summary>
    public static IEnumerable<Guid> Excluded(PackageContents contents, bool includeKeys) => contents.Items
        .Where(item => NeverSynced.Contains(item.Type) || (!includeKeys && item.Type == ImportExportRecordType.Key))
        .Select(item => item.Id);
}
