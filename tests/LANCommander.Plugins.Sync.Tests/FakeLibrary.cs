using System.Text.Json;
using LANCommander.SDK.Enums;
using LANCommander.Server.Plugins;

namespace LANCommander.Plugins.Sync.Tests;

/// <summary>
/// A server's games, redistributables and tools in memory, behind the host contracts the plugin uses.
/// Its "packages" are JSON naming the record and the child records they carry, and importing one adds
/// or updates the record the way the server's import does: the record keeps its id, and gets a new
/// timestamp.
/// </summary>
internal sealed class FakeLibrary :
    IGameProvider,
    IRedistributableProvider,
    IToolProvider,
    ITaxonomyProvider,
    IArchiveProvider,
    IPackageExporter,
    IPackageImporter
{
    private readonly Lock _lock = new();
    private DateTime _clock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public Dictionary<Guid, Record> Records { get; } = [];
    public Dictionary<Guid, TaxonomyInfo> Collections { get; } = [];

    /// <summary>Every package exported, with the child records it left out.</summary>
    public List<(Guid Id, IReadOnlySet<Guid> Excluded)> Exports { get; } = [];

    /// <summary>Every package imported, in order.</summary>
    public List<Guid> Imports { get; } = [];

    /// <summary>Makes the next import of the record fail.</summary>
    public HashSet<Guid> FailImports { get; } = [];

    internal sealed class Record
    {
        public required ImportExportRecordType Type { get; init; }
        public required Guid Id { get; init; }
        public required string Name { get; set; }
        public Guid? BaseGameId { get; set; }
        public DateTime UpdatedOn { get; set; }
        public HashSet<Guid> Collections { get; } = [];
        public HashSet<Guid> Redistributables { get; } = [];
        public HashSet<Guid> Tools { get; } = [];
        public List<PackageItem> Items { get; set; } = [];
    }

    DateTime Tick() => _clock = _clock.AddMinutes(1);

    public Record Add(ImportExportRecordType type, string name, Guid? baseGameId = null, params PackageItem[] items)
    {
        var record = new Record { Type = type, Id = Guid.NewGuid(), Name = name, BaseGameId = baseGameId, UpdatedOn = Tick(), Items = [.. items] };

        Records[record.Id] = record;

        return record;
    }

    public TaxonomyInfo AddCollection(string name)
    {
        var collection = new TaxonomyInfo { Id = Guid.NewGuid(), Kind = TaxonomyKind.Collection, Name = name };

        Collections[collection.Id] = collection;

        return collection;
    }

    /// <summary>Changes the record, as editing it on the server would.</summary>
    public void Touch(Guid id) => Records[id].UpdatedOn = Tick();

    public PackageItem Item(ImportExportRecordType type, long size = 0, string name = "") =>
        new(Guid.NewGuid(), type, name, size, Tick());

    IEnumerable<Record> OfType(ImportExportRecordType type) => Records.Values.Where(r => r.Type == type).OrderBy(r => r.Name);

    static GameInfo ToGame(Record r) => new() { Id = r.Id, Title = r.Name, BaseGameId = r.BaseGameId, UpdatedOn = r.UpdatedOn };

    // IPackageExporter

    public Task<PackageContents> DescribeAsync(ImportExportRecordType recordType, Guid recordId, CancellationToken cancellationToken = default)
    {
        if (!Records.TryGetValue(recordId, out var record) || record.Type != recordType)
            throw new KeyNotFoundException($"There is no {recordType} {recordId}.");

        return Task.FromResult(new PackageContents(record.Id, record.Type, record.Name, record.UpdatedOn, record.Items.ToList()));
    }

    public async Task ExportAsync(ImportExportRecordType recordType, Guid recordId, Stream output, IEnumerable<Guid>? excludedItemIds = null, CancellationToken cancellationToken = default)
    {
        var contents = await DescribeAsync(recordType, recordId, cancellationToken);
        var excluded = (excludedItemIds ?? []).ToHashSet();
        var record = Records[recordId];

        lock (_lock)
            Exports.Add((recordId, excluded));

        var package = new FakePackage(
            record.Type,
            record.Id,
            record.Name,
            record.BaseGameId,
            contents.Items.Where(i => !excluded.Contains(i.Id)).ToList());

        // Synchronously, as the server's zip writer does
        JsonSerializer.Serialize(output, package);
    }

    // IPackageImporter

    public Task<PackageImportResult> ImportFileAsync(string packagePath, Guid? storageLocationId = null, ManifestType? manifestType = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async Task<PackageImportResult> ImportStreamAsync(Stream package, Guid? storageLocationId = null, ManifestType? manifestType = null, IProgress<long>? copyProgress = null, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();

        await package.CopyToAsync(buffer, cancellationToken);
        copyProgress?.Report(buffer.Length);

        var imported = JsonSerializer.Deserialize<FakePackage>(buffer.ToArray())!;

        if (FailImports.Remove(imported.Id))
            throw new InvalidOperationException($"Import of {imported.Name} failed");

        lock (_lock)
        {
            Imports.Add(imported.Id);

            if (!Records.TryGetValue(imported.Id, out var record))
                Records[imported.Id] = record = new Record { Type = imported.Type, Id = imported.Id, Name = imported.Name };

            record.Name = imported.Name;
            record.BaseGameId = imported.BaseGameId;
            record.UpdatedOn = Tick();

            // Imports add and update child records, and never remove them
            foreach (var item in imported.Items)
            {
                record.Items.RemoveAll(i => i.Id == item.Id);
                record.Items.Add(item with { UpdatedOn = Tick() });
            }
        }

        return new PackageImportResult(imported.Id, manifestType ?? ManifestType.Game, 1);
    }

    sealed record FakePackage(ImportExportRecordType Type, Guid Id, string Name, Guid? BaseGameId, List<PackageItem> Items);

    // IGameProvider

    Task<IReadOnlyList<GameInfo>> IGameProvider.GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GameInfo>>(OfType(ImportExportRecordType.Game).Select(ToGame).ToList());

    Task<GameInfo?> IGameProvider.GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Records.TryGetValue(id, out var r) && r.Type == ImportExportRecordType.Game ? ToGame(r) : null);

    public Task<IReadOnlyList<Guid>> GetLinksAsync(Guid gameId, GameLinkKind kind, CancellationToken cancellationToken = default)
    {
        var game = Records[gameId];

        IReadOnlyList<Guid> links = kind switch
        {
            GameLinkKind.Collections => game.Collections.ToList(),
            GameLinkKind.Redistributables => game.Redistributables.ToList(),
            GameLinkKind.Tools => game.Tools.ToList(),
            _ => [],
        };

        return Task.FromResult(links);
    }

    public Task<IReadOnlyList<GameInfo>> GetAddonsAsync(Guid baseGameId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<GameInfo?> FindByExternalIdAsync(string provider, string externalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<GameInfo> AddAsync(GameInfo game, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<GameInfo> UpdateAsync(GameInfo game, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task IGameProvider.DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SetLinksAsync(Guid gameId, GameLinkKind kind, IEnumerable<Guid> ids, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string?> GetRedistributableOptionsAsync(Guid gameId, Guid redistributableId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetRedistributableOptionsAsync(Guid gameId, Guid redistributableId, string? optionsJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<GameExternalIdInfo>> GetExternalIdsAsync(Guid gameId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<GameExternalIdInfo> SetExternalIdAsync(Guid gameId, string provider, string externalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task RemoveExternalIdAsync(Guid gameId, string provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<GameCustomFieldInfo>> GetCustomFieldsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<GameCustomFieldInfo>> GetCustomFieldsAsync(Guid gameId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<GameCustomFieldInfo> SetCustomFieldAsync(Guid gameId, string name, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task RemoveCustomFieldAsync(Guid gameId, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    // IRedistributableProvider

    Task<IReadOnlyList<RedistributableInfo>> IRedistributableProvider.GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RedistributableInfo>>(OfType(ImportExportRecordType.Redistributable).Select(r => new RedistributableInfo { Id = r.Id, Name = r.Name }).ToList());

    Task<RedistributableInfo?> IRedistributableProvider.GetAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<RedistributableInfo?> IRedistributableProvider.FindAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<RedistributableInfo> AddAsync(RedistributableInfo redistributable, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RedistributableInfo> UpdateAsync(RedistributableInfo redistributable, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task IRedistributableProvider.DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

    // IToolProvider

    Task<IReadOnlyList<ToolInfo>> IToolProvider.GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ToolInfo>>(OfType(ImportExportRecordType.Tool).Select(r => new ToolInfo { Id = r.Id, Name = r.Name }).ToList());

    Task<ToolInfo?> IToolProvider.GetAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<ToolInfo?> IToolProvider.FindAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ToolInfo> AddAsync(ToolInfo tool, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ToolInfo> UpdateAsync(ToolInfo tool, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task IToolProvider.DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<Guid>> GetGamesAsync(Guid toolId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetGamesAsync(Guid toolId, IEnumerable<Guid> gameIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    // ITaxonomyProvider

    Task<IReadOnlyList<TaxonomyInfo>> ITaxonomyProvider.GetAsync(TaxonomyKind kind, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TaxonomyInfo>>(kind == TaxonomyKind.Collection ? Collections.Values.OrderBy(c => c.Name).ToList() : []);

    Task<TaxonomyInfo?> ITaxonomyProvider.GetAsync(TaxonomyKind kind, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<TaxonomyInfo?> ITaxonomyProvider.FindAsync(TaxonomyKind kind, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<TaxonomyInfo> GetOrAddAsync(TaxonomyKind kind, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TaxonomyInfo> AddAsync(TaxonomyKind kind, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TaxonomyInfo> RenameAsync(TaxonomyKind kind, Guid id, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task ITaxonomyProvider.DeleteAsync(TaxonomyKind kind, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

    // IArchiveProvider

    public Task<IReadOnlyList<ArchiveInfo>> GetAsync(RecordOwner owner, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ArchiveInfo?> GetArchiveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var archive = Records.Values
            .SelectMany(r => r.Items)
            .FirstOrDefault(i => i.Type == ImportExportRecordType.Archive && i.Id == id);

        return Task.FromResult(archive == null ? null : new ArchiveInfo { Id = archive.Id, CompressedSize = archive.Size });
    }

    Task IArchiveProvider.DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
}
