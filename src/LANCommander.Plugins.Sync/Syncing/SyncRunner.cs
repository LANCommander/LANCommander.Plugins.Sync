using LANCommander.Plugins.Sync.Data;
using LANCommander.Plugins.Sync.Protocol;
using LANCommander.Plugins.Sync.Remotes;
using LANCommander.Plugins.Sync.Source;
using LANCommander.SDK.Enums;
using LANCommander.Server.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Sync.Syncing;

/// <summary>
/// Syncs from one remote server: asks it for the records its filters let through, and imports those
/// that are new or have changed there since they were last imported.
/// </summary>
/// <remarks>
/// <para>
/// Each record arrives as an LCX package exported by the remote server and imported here through the
/// server's own import pipeline, so a sync is exactly an export there and an import here. Archives
/// this server already holds are left out of the package.
/// </para>
/// <para>
/// Imports only add and update. A record deleted on the remote server stays here, and so does a
/// child record, such as a script, removed from a record that's still synced.
/// </para>
/// </remarks>
public sealed class SyncRunner(
    RemoteClient client,
    RemoteServers remotes,
    SyncContext data,
    IServiceScopeFactory scopes,
    IArchiveProvider archives,
    IGameProvider games,
    IRedistributableProvider redistributables,
    IToolProvider tools,
    ILogger<SyncRunner> logger)
{
    public async Task<SyncRun> RunAsync(RemoteServer remote, SyncProgress progress, CancellationToken cancellationToken)
    {
        var run = new SyncRun
        {
            Id = Guid.NewGuid(),
            RemoteId = remote.Id,
            Trigger = progress.Trigger,
            Status = SyncRunStatus.Running,
            StartedOn = DateTime.UtcNow,
        };

        data.Runs.Add(run);
        await data.SaveChangesAsync(CancellationToken.None);

        try
        {
            await SyncAsync(remote, run, progress, cancellationToken);

            run.Status = run.Failed > 0 ? SyncRunStatus.CompletedWithErrors : SyncRunStatus.Succeeded;
        }
        // Whatever a cancelled request fails with; not every handler throws OperationCanceledException
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            run.Status = SyncRunStatus.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Syncing from {Remote} failed", remote.Name);

            run.Status = SyncRunStatus.Failed;
            run.Error = Truncate(ex is RemoteServerException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
        }

        run.FinishedOn = DateTime.UtcNow;
        await data.SaveChangesAsync(CancellationToken.None);

        logger.LogInformation(
            "Synced from {Remote}: {Status}, {Checked} checked, {Changed} changed, {Imported} imported, {Failed} failed",
            remote.Name, run.Status, run.Checked, run.Changed, run.Imported, run.Failed);

        return run;
    }

    async Task SyncAsync(RemoteServer remote, SyncRun run, SyncProgress progress, CancellationToken cancellationToken)
    {
        var instanceId = remotes.InstanceId;
        var info = await client.GetInfoAsync(remote, cancellationToken);

        if (info.InstanceId == instanceId)
            throw new RemoteServerException($"{remote.Name} has this server's sync instance id. Either it is this server, or one was copied from the other: remove InstanceId from the Sync plugin's Settings.yaml on one of them and restart it.");

        if (info.ProtocolVersion != SyncProtocol.Version)
            throw new RemoteServerException($"{remote.Name} runs version {info.PluginVersion} of the Sync plugin, which can't sync with this server's {SyncPlugin.PluginVersion}. Update the older one.");

        progress.ReadingCatalog();

        var catalog = await client.GetCatalogAsync(remote, new CatalogRequest(
            instanceId,
            remote.Games,
            remote.CollectionIds,
            remote.Redistributables,
            remote.Tools), cancellationToken);

        var synced = await data.Records
            .Where(r => r.RemoteId == remote.Id)
            .ToDictionaryAsync(r => (r.RecordType, r.RecordId), cancellationToken);

        var existing = await LoadExistingAsync(cancellationToken);

        run.Checked = catalog.Entries.Count;

        var changed = catalog.Entries
            .Where(entry => entry.OriginId != instanceId)
            .Where(entry => !synced.TryGetValue((entry.Type, entry.Id), out var record)
                            || record.RemoteFingerprint != entry.Fingerprint
                            // Deleted here since it was synced
                            || !existing.Contains((entry.Type, entry.Id)))
            .ToList();

        run.Changed = changed.Count;
        progress.Importing(changed.Count);
        await data.SaveChangesAsync(CancellationToken.None);

        foreach (var entry in changed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!synced.TryGetValue((entry.Type, entry.Id), out var record))
            {
                record = new SyncedRecord
                {
                    Id = Guid.NewGuid(),
                    RemoteId = remote.Id,
                    RecordType = entry.Type,
                    RecordId = entry.Id,
                };

                data.Records.Add(record);
                synced[(entry.Type, entry.Id)] = record;
            }

            record.Name = Truncate(entry.Name, 256)!;
            record.LastAttemptOn = DateTime.UtcNow;

            try
            {
                var downloaded = await ImportAsync(remote, entry, info, record, progress, cancellationToken);

                run.Imported++;
                run.Downloaded += downloaded;
                progress.Finished(true);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                record.LastError = "The sync was cancelled before this finished.";
                await data.SaveChangesAsync(CancellationToken.None);

                throw;
            }
            catch (RemoteServerException ex) when (ex.StatusCode == null)
            {
                // The remote server has gone away; every other record would fail the same way
                record.LastError = Truncate(ex.Message);
                run.Failed++;
                await data.SaveChangesAsync(CancellationToken.None);

                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not sync {Type} {Name} ({Id}) from {Remote}", entry.Type, entry.Name, entry.Id, remote.Name);

                record.LastError = Truncate(ex is RemoteServerException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
                run.Failed++;
                progress.Finished(false);
            }

            await data.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>Downloads and imports one record, then records how it stands on both servers.</summary>
    /// <returns>Bytes downloaded.</returns>
    async Task<long> ImportAsync(RemoteServer remote, CatalogEntry entry, RemoteInfo info, SyncedRecord record, SyncProgress progress, CancellationToken cancellationToken)
    {
        var request = new ExportRequest(entry.Type, entry.Id, await HeldArchivesAsync(entry, cancellationToken), remote.IncludeKeys);

        using var package = await client.OpenPackageAsync(remote, request, cancellationToken);

        // A scope per import: the server's importers keep state between imports in a scope, and a
        // sync can import hundreds of records
        await using var scope = scopes.CreateAsyncScope();

        var importer = scope.ServiceProvider.GetRequiredService<IPackageImporter>();
        var exporter = scope.ServiceProvider.GetRequiredService<IPackageExporter>();

        progress.Starting(entry.Name, package.Length);

        long downloaded = 0;

        var result = await importer.ImportStreamAsync(
            package.Content,
            remote.StorageLocationId,
            ToManifestType(entry.Type),
            new InlineProgress(bytes =>
            {
                downloaded = bytes;
                progress.Downloaded(bytes);
            }),
            cancellationToken);

        if (result.RecordId != entry.Id)
            logger.LogWarning(
                "{Type} {Name} from {Remote} was imported into {LocalId} rather than {Id}, a record here with the same name",
                entry.Type, entry.Name, remote.Name, result.RecordId, entry.Id);

        var local = await exporter.DescribeAsync(entry.Type, result.RecordId, cancellationToken);

        record.RemoteFingerprint = entry.Fingerprint;
        record.LocalFingerprint = Fingerprints.Compute(local);
        // Passed along from server to server, so a record that goes round a ring of servers stops where it started
        record.OriginId = entry.OriginId ?? info.InstanceId;
        record.SyncedOn = DateTime.UtcNow;
        record.LastError = null;

        return downloaded;
    }

    /// <summary>The record's archives this server already holds, which the package can leave out.</summary>
    async Task<IReadOnlyList<Guid>> HeldArchivesAsync(CatalogEntry entry, CancellationToken cancellationToken)
    {
        var held = new List<Guid>();

        foreach (var archive in entry.Archives)
        {
            var local = await archives.GetArchiveAsync(archive.Id, cancellationToken);

            // A size that's known on both sides and differs means the file was replaced
            if (local != null && (archive.Size <= 0 || local.CompressedSize <= 0 || archive.Size == local.CompressedSize))
                held.Add(archive.Id);
        }

        return held;
    }

    async Task<HashSet<(ImportExportRecordType, Guid)>> LoadExistingAsync(CancellationToken cancellationToken)
    {
        var existing = new HashSet<(ImportExportRecordType, Guid)>();

        foreach (var game in await games.GetAsync(cancellationToken))
            existing.Add((ImportExportRecordType.Game, game.Id));

        foreach (var redistributable in await redistributables.GetAsync(cancellationToken))
            existing.Add((ImportExportRecordType.Redistributable, redistributable.Id));

        foreach (var tool in await tools.GetAsync(cancellationToken))
            existing.Add((ImportExportRecordType.Tool, tool.Id));

        return existing;
    }

    static ManifestType ToManifestType(ImportExportRecordType type) => type switch
    {
        ImportExportRecordType.Game => ManifestType.Game,
        ImportExportRecordType.Redistributable => ManifestType.Redistributable,
        ImportExportRecordType.Tool => ManifestType.Tool,
        _ => throw new ArgumentException($"{type} records aren't synced.", nameof(type)),
    };

    static string? Truncate(string? value, int length = 2000) =>
        value == null || value.Length <= length ? value : value[..(length - 1)] + "…";

    /// <summary>Reports on the calling thread, unlike <see cref="Progress{T}"/>, which posts to the thread pool.</summary>
    sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
