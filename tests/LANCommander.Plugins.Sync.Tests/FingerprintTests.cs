using LANCommander.Plugins.Sync.Source;
using LANCommander.SDK.Enums;
using LANCommander.Server.Plugins;

namespace LANCommander.Plugins.Sync.Tests;

public sealed class FingerprintTests
{
    static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    static PackageContents Game(params PackageItem[] items) =>
        new(Guid.Parse("8c1f5b1e-6a43-4a7f-9b58-1c9a5e0d2f11"), ImportExportRecordType.Game, "Game", Now, items);

    static PackageItem Item(ImportExportRecordType type, string id, long size = 0, DateTime? updatedOn = null) =>
        new(Guid.Parse(id), type, "", size, updatedOn ?? Now);

    readonly PackageItem _archive = Item(ImportExportRecordType.Archive, "00000000-0000-0000-0000-000000000001", 100);
    readonly PackageItem _script = Item(ImportExportRecordType.Script, "00000000-0000-0000-0000-000000000002");

    [Fact]
    public void TheOrderOfChildRecordsDoesntMatter() =>
        Assert.Equal(Fingerprints.Compute(Game(_archive, _script)), Fingerprints.Compute(Game(_script, _archive)));

    [Fact]
    public void ChangingTheRecordChangesIt() =>
        Assert.NotEqual(Fingerprints.Compute(Game(_archive)), Fingerprints.Compute(Game(_archive) with { UpdatedOn = Now.AddSeconds(1) }));

    [Fact]
    public void AddingRemovingOrChangingAChildRecordChangesIt()
    {
        var original = Fingerprints.Compute(Game(_archive));

        Assert.NotEqual(original, Fingerprints.Compute(Game(_archive, _script)));
        Assert.NotEqual(original, Fingerprints.Compute(Game()));
        Assert.NotEqual(original, Fingerprints.Compute(Game(_archive with { Size = 101 })));
        Assert.NotEqual(original, Fingerprints.Compute(Game(_archive with { UpdatedOn = Now.AddDays(1) })));
    }

    [Fact]
    public void SavesAndPlaySessionsArentPartOfIt()
    {
        var save = Item(ImportExportRecordType.Save, "00000000-0000-0000-0000-000000000003", 5000);
        var session = Item(ImportExportRecordType.PlaySession, "00000000-0000-0000-0000-000000000004");

        Assert.Equal(Fingerprints.Compute(Game(_archive)), Fingerprints.Compute(Game(_archive, save, session)));
    }

    [Fact]
    public void KeysCountByIdAlone()
    {
        var key = Item(ImportExportRecordType.Key, "00000000-0000-0000-0000-000000000005");

        Assert.NotEqual(Fingerprints.Compute(Game()), Fingerprints.Compute(Game(key)));
        Assert.Equal(Fingerprints.Compute(Game(key)), Fingerprints.Compute(Game(key with { UpdatedOn = Now.AddDays(3) })));
    }

    [Fact]
    public void SavesPlaySessionsAndUnwantedKeysAreLeftOutOfPackages()
    {
        var save = Item(ImportExportRecordType.Save, "00000000-0000-0000-0000-000000000003");
        var session = Item(ImportExportRecordType.PlaySession, "00000000-0000-0000-0000-000000000004");
        var key = Item(ImportExportRecordType.Key, "00000000-0000-0000-0000-000000000005");
        var game = Game(_archive, save, session, key);

        Assert.Equal([save.Id, session.Id, key.Id], Fingerprints.Excluded(game, includeKeys: false));
        Assert.Equal([save.Id, session.Id], Fingerprints.Excluded(game, includeKeys: true));
    }
}
