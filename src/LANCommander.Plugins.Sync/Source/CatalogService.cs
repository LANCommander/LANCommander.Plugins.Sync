using LANCommander.Plugins.Sync.Data;
using LANCommander.Plugins.Sync.Protocol;
using LANCommander.Plugins.Sync.Remotes;
using LANCommander.SDK.Enums;
using LANCommander.Server.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Sync.Source;

/// <summary>
/// Answers the servers that sync from this one: which records their filters let through, how each
/// stands, and packages of the ones they ask for.
/// </summary>
public sealed class CatalogService(
    IGameProvider games,
    IRedistributableProvider redistributables,
    IToolProvider tools,
    ITaxonomyProvider taxonomy,
    IPackageExporter exporter,
    SyncContext data,
    ILogger<CatalogService> logger)
{
    public async Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken) =>
        (await taxonomy.GetAsync(TaxonomyKind.Collection, cancellationToken))
            .Select(collection => new CollectionInfo(collection.Id, collection.Name))
            .ToList();

    public async Task<CatalogResponse> GetCatalogAsync(CatalogRequest request, CancellationToken cancellationToken)
    {
        var selectedGames = await SelectGamesAsync(request, cancellationToken);

        var redistributableIds = await SelectDependenciesAsync(
            request.Redistributables,
            selectedGames,
            GameLinkKind.Redistributables,
            async () => (await redistributables.GetAsync(cancellationToken)).Select(r => r.Id),
            cancellationToken);

        var toolIds = await SelectDependenciesAsync(
            request.Tools,
            selectedGames,
            GameLinkKind.Tools,
            async () => (await tools.GetAsync(cancellationToken)).Select(t => t.Id),
            cancellationToken);

        var origins = await LoadOriginsAsync(cancellationToken);
        var entries = new List<CatalogEntry>();

        // Games are imported last, so the redistributables and tools they link to are there already
        foreach (var id in redistributableIds)
            await AddAsync(entries, ImportExportRecordType.Redistributable, id, null, origins, cancellationToken);

        foreach (var id in toolIds)
            await AddAsync(entries, ImportExportRecordType.Tool, id, null, origins, cancellationToken);

        foreach (var game in selectedGames)
            await AddAsync(entries, ImportExportRecordType.Game, game.Id, game.BaseGameId, origins, cancellationToken);

        return new CatalogResponse(entries);
    }

    /// <summary>
    /// Describes the record before anything is written, so a record that doesn't exist is reported as
    /// such rather than as a broken package. The returned function writes the package.
    /// </summary>
    /// <exception cref="KeyNotFoundException">There is no such record.</exception>
    /// <exception cref="ArgumentException">The record type isn't one that is synced.</exception>
    public async Task<Func<Stream, CancellationToken, Task>> PrepareExportAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        if (request.Type is not (ImportExportRecordType.Game or ImportExportRecordType.Redistributable or ImportExportRecordType.Tool))
            throw new ArgumentException($"{request.Type} records aren't synced.", nameof(request));

        var contents = await exporter.DescribeAsync(request.Type, request.Id, cancellationToken);

        var excluded = Fingerprints.Excluded(contents, request.IncludeKeys)
            .Concat(request.ExcludedItemIds ?? [])
            .ToHashSet();

        return (output, token) => exporter.ExportAsync(request.Type, request.Id, output, excluded, token);
    }

    async Task<IReadOnlyList<GameInfo>> SelectGamesAsync(CatalogRequest request, CancellationToken cancellationToken)
    {
        if (request.Games == GameSyncMode.None)
            return [];

        var all = await games.GetAsync(cancellationToken);

        if (request.Games == GameSyncMode.All)
            return OrderForImport(all);

        var collections = (request.CollectionIds ?? []).ToHashSet();
        var selected = new HashSet<Guid>();

        foreach (var game in all)
        {
            var links = await games.GetLinksAsync(game.Id, GameLinkKind.Collections, cancellationToken);

            if (links.Any(collections.Contains))
                selected.Add(game.Id);
        }

        return OrderForImport(WithRelatives(all, selected));
    }

    /// <summary>
    /// The selected games with their base games and add-ons, wherever a collection drew the line: an
    /// add-on can't be imported without its game, and a game is incomplete without its add-ons.
    /// </summary>
    internal static IReadOnlyList<GameInfo> WithRelatives(IReadOnlyList<GameInfo> all, IReadOnlySet<Guid> selection)
    {
        var selected = new HashSet<Guid>(selection);
        var known = all.Select(g => g.Id).ToHashSet();
        bool grew;

        do
        {
            grew = false;

            foreach (var game in all)
            {
                if (game.BaseGameId is not { } baseId || !known.Contains(baseId))
                    continue;

                if (selected.Contains(game.Id))
                    grew |= selected.Add(baseId);
                else if (selected.Contains(baseId))
                    grew |= selected.Add(game.Id);
            }
        }
        while (grew);

        return all.Where(g => selected.Contains(g.Id)).ToList();
    }

    /// <summary>Base games before their add-ons, since an add-on's import links it to its game.</summary>
    internal static IReadOnlyList<GameInfo> OrderForImport(IReadOnlyList<GameInfo> selection)
    {
        var byId = selection.ToDictionary(g => g.Id);

        int Depth(GameInfo game)
        {
            var depth = 0;
            var seen = new HashSet<Guid> { game.Id };

            while (game.BaseGameId is { } baseId && byId.TryGetValue(baseId, out var parent) && seen.Add(parent.Id))
            {
                depth++;
                game = parent;
            }

            return depth;
        }

        return selection
            .OrderBy(Depth)
            .ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    async Task<IReadOnlyList<Guid>> SelectDependenciesAsync(
        DependencySyncMode mode,
        IReadOnlyList<GameInfo> selectedGames,
        GameLinkKind kind,
        Func<Task<IEnumerable<Guid>>> all,
        CancellationToken cancellationToken)
    {
        switch (mode)
        {
            case DependencySyncMode.All:
                return (await all()).ToList();

            case DependencySyncMode.UsedByGames:
                var used = new HashSet<Guid>();

                foreach (var game in selectedGames)
                    used.UnionWith(await games.GetLinksAsync(game.Id, kind, cancellationToken));

                // In the order the server lists them, which is by name
                return (await all()).Where(used.Contains).ToList();

            default:
                return [];
        }
    }

    async Task AddAsync(
        List<CatalogEntry> entries,
        ImportExportRecordType type,
        Guid id,
        Guid? baseGameId,
        ILookup<(ImportExportRecordType, Guid), SyncedRecord> origins,
        CancellationToken cancellationToken)
    {
        PackageContents contents;

        try
        {
            contents = await exporter.DescribeAsync(type, id, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            // Deleted while the catalog was being put together
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not describe {Type} {Id} for a sync; it's left out of the catalog", type, id);
            return;
        }

        var fingerprint = Fingerprints.Compute(contents);

        // Still exactly as it was synced from elsewhere, so it isn't this server's to offer as its own
        var origin = origins[(type, id)].FirstOrDefault(r => r.LocalFingerprint == fingerprint)?.OriginId;

        entries.Add(new CatalogEntry(
            type,
            id,
            contents.Name,
            baseGameId,
            fingerprint,
            origin,
            contents.Items
                .Where(item => item.Type == ImportExportRecordType.Archive)
                .Select(item => new CatalogArchive(item.Id, item.Size))
                .ToList()));
    }

    async Task<ILookup<(ImportExportRecordType, Guid), SyncedRecord>> LoadOriginsAsync(CancellationToken cancellationToken)
    {
        var records = await data.Records
            .AsNoTracking()
            .Where(r => r.LocalFingerprint != null && r.OriginId != null)
            .ToListAsync(cancellationToken);

        return records.ToLookup(r => (r.RecordType, r.RecordId));
    }
}
