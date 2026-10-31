using LANCommander.Plugins.Sync.Data;
using LANCommander.Plugins.Sync.Remotes;
using LANCommander.SDK.Enums;

namespace LANCommander.Plugins.Sync.Tests;

/// <summary>Two or three servers syncing over HTTP, through the plugin's own endpoints.</summary>
public sealed class SyncTests : IAsyncLifetime
{
    private readonly Network _network = new();
    private SyncServer _hq = default!;
    private SyncServer _site = default!;

    public async Task InitializeAsync()
    {
        _hq = await SyncServer.StartAsync("hq", _network);
        _site = await SyncServer.StartAsync("site", _network);
    }

    public async Task DisposeAsync()
    {
        await _hq.DisposeAsync();
        await _site.DisposeAsync();
    }

    [Fact]
    public async Task TheFirstSyncImportsEverythingWithDependenciesFirstAndBaseGamesBeforeAddons()
    {
        var addon = _hq.Library.Add(ImportExportRecordType.Game, "A Expansion");
        var game = _hq.Library.Add(ImportExportRecordType.Game, "Z Game");
        var redistributable = _hq.Library.Add(ImportExportRecordType.Redistributable, "DirectX");
        var tool = _hq.Library.Add(ImportExportRecordType.Tool, "Mod Manager");

        addon.BaseGameId = game.Id;

        var remote = _site.AddRemote(_hq);
        var run = await _site.SyncFromAsync(remote);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(4, run.Checked);
        Assert.Equal(4, run.Imported);
        Assert.Equal([redistributable.Id, tool.Id, game.Id, addon.Id], _site.Library.Imports);
        Assert.All(await _site.SyncedRecordsAsync(), record => Assert.Null(record.LastError));
    }

    [Fact]
    public async Task OnlyRecordsChangedSinceTheLastSyncAreImported()
    {
        var first = _hq.Library.Add(ImportExportRecordType.Game, "First");
        var second = _hq.Library.Add(ImportExportRecordType.Game, "Second");

        var remote = _site.AddRemote(_hq);

        await _site.SyncFromAsync(remote);
        _site.Library.Imports.Clear();

        var unchanged = await _site.SyncFromAsync(remote);

        Assert.Equal(2, unchanged.Checked);
        Assert.Equal(0, unchanged.Changed);
        Assert.Empty(_site.Library.Imports);

        _hq.Library.Touch(second.Id);

        var changed = await _site.SyncFromAsync(remote);

        Assert.Equal(1, changed.Changed);
        Assert.Equal([second.Id], _site.Library.Imports);
        Assert.DoesNotContain(first.Id, _site.Library.Imports);
    }

    [Fact]
    public async Task AChangedChildRecordCountsAsAChange()
    {
        var script = _hq.Library.Item(ImportExportRecordType.Script);
        var game = _hq.Library.Add(ImportExportRecordType.Game, "Game", null, script);
        var remote = _site.AddRemote(_hq);

        await _site.SyncFromAsync(remote);
        _site.Library.Imports.Clear();

        game.Items[0] = script with { UpdatedOn = script.UpdatedOn!.Value.AddHours(1) };

        await _site.SyncFromAsync(remote);

        Assert.Equal([game.Id], _site.Library.Imports);
    }

    [Fact]
    public async Task SavesAndPlaySessionsNeverTravelOrCountAsChanges()
    {
        var save = _hq.Library.Item(ImportExportRecordType.Save, 1024);
        var session = _hq.Library.Item(ImportExportRecordType.PlaySession);
        var game = _hq.Library.Add(ImportExportRecordType.Game, "Game", null, save, session);
        var remote = _site.AddRemote(_hq);

        await _site.SyncFromAsync(remote);

        Assert.Contains(save.Id, _hq.Library.Exports.Single().Excluded);
        Assert.Contains(session.Id, _hq.Library.Exports.Single().Excluded);
        Assert.Empty(_site.Library.Records[game.Id].Items);

        // A player playing isn't a change to sync
        game.Items.Add(_hq.Library.Item(ImportExportRecordType.PlaySession));
        _site.Library.Imports.Clear();

        await _site.SyncFromAsync(remote);

        Assert.Empty(_site.Library.Imports);
    }

    [Fact]
    public async Task KeysTravelOnlyWhenAskedForAndClaimingOneIsNoChange()
    {
        var key = _hq.Library.Item(ImportExportRecordType.Key);
        var game = _hq.Library.Add(ImportExportRecordType.Game, "Game", null, key);

        var withoutKeys = _site.AddRemote(_hq);

        await _site.SyncFromAsync(withoutKeys);

        Assert.Contains(key.Id, _hq.Library.Exports.Last().Excluded);

        await using var other = await SyncServer.StartAsync("other", _network);

        var withKeys = other.AddRemote(_hq, r => r.IncludeKeys = true);

        await other.SyncFromAsync(withKeys);

        Assert.DoesNotContain(key.Id, _hq.Library.Exports.Last().Excluded);

        // Claimed by a player
        game.Items[0] = key with { UpdatedOn = key.UpdatedOn!.Value.AddDays(1) };
        other.Library.Imports.Clear();

        await other.SyncFromAsync(withKeys);

        Assert.Empty(other.Library.Imports);
    }

    [Fact]
    public async Task ArchivesThisServerHoldsArentDownloadedAgain()
    {
        var archive = _hq.Library.Item(ImportExportRecordType.Archive, 4_000_000_000);
        var game = _hq.Library.Add(ImportExportRecordType.Game, "Game", null, archive);
        var remote = _site.AddRemote(_hq);

        await _site.SyncFromAsync(remote);

        Assert.DoesNotContain(archive.Id, _hq.Library.Exports.Last().Excluded);

        _hq.Library.Touch(game.Id);

        await _site.SyncFromAsync(remote);

        Assert.Contains(archive.Id, _hq.Library.Exports.Last().Excluded);

        // Same archive, different file
        game.Items[0] = archive with { Size = 5_000_000_000 };
        _hq.Library.Touch(game.Id);

        await _site.SyncFromAsync(remote);

        Assert.DoesNotContain(archive.Id, _hq.Library.Exports.Last().Excluded);
    }

    [Fact]
    public async Task CollectionsFilterGamesAndBringTheirAddonsBaseGamesAndWhatTheyUse()
    {
        var lanParty = _hq.Library.AddCollection("LAN Party");
        var redistributable = _hq.Library.Add(ImportExportRecordType.Redistributable, "VC++");
        var unused = _hq.Library.Add(ImportExportRecordType.Redistributable, "Unused");
        var inCollection = _hq.Library.Add(ImportExportRecordType.Game, "In Collection");
        var addon = _hq.Library.Add(ImportExportRecordType.Game, "Its Add-on", inCollection.Id);
        var baseGame = _hq.Library.Add(ImportExportRecordType.Game, "Base Of An Add-on In The Collection");
        var addonInCollection = _hq.Library.Add(ImportExportRecordType.Game, "Add-on In The Collection", baseGame.Id);
        var elsewhere = _hq.Library.Add(ImportExportRecordType.Game, "Not In It");

        inCollection.Collections.Add(lanParty.Id);
        inCollection.Redistributables.Add(redistributable.Id);
        addonInCollection.Collections.Add(lanParty.Id);

        var remote = _site.AddRemote(_hq, r =>
        {
            r.Games = GameSyncMode.Collections;
            r.CollectionIds = [lanParty.Id];
            r.Redistributables = DependencySyncMode.UsedByGames;
            r.Tools = DependencySyncMode.None;
        });

        await _site.SyncFromAsync(remote);

        Assert.Equal(
            new[] { redistributable.Id, inCollection.Id, addon.Id, baseGame.Id, addonInCollection.Id }.Order(),
            _site.Library.Imports.Order());
        Assert.DoesNotContain(elsewhere.Id, _site.Library.Imports);
        Assert.DoesNotContain(unused.Id, _site.Library.Imports);
        Assert.Equal(redistributable.Id, _site.Library.Imports[0]);
    }

    [Fact]
    public async Task ARecordDeletedHereIsBroughtBack()
    {
        var game = _hq.Library.Add(ImportExportRecordType.Game, "Game");
        var remote = _site.AddRemote(_hq);

        await _site.SyncFromAsync(remote);

        _site.Library.Records.Remove(game.Id);
        _site.Library.Imports.Clear();

        await _site.SyncFromAsync(remote);

        Assert.Equal([game.Id], _site.Library.Imports);
    }

    [Fact]
    public async Task AFailedImportIsRecordedAndRetriedNextTime()
    {
        var broken = _hq.Library.Add(ImportExportRecordType.Game, "Broken");
        var fine = _hq.Library.Add(ImportExportRecordType.Game, "Fine");
        var remote = _site.AddRemote(_hq);

        _site.Library.FailImports.Add(broken.Id);

        var run = await _site.SyncFromAsync(remote);

        Assert.Equal(SyncRunStatus.CompletedWithErrors, run.Status);
        Assert.Equal(1, run.Imported);
        Assert.Equal(1, run.Failed);
        Assert.Contains("Import of Broken failed", (await _site.SyncedRecordsAsync()).Single(r => r.RecordId == broken.Id).LastError);

        _site.Library.Imports.Clear();

        var retry = await _site.SyncFromAsync(remote);

        Assert.Equal(SyncRunStatus.Succeeded, retry.Status);
        Assert.Equal([broken.Id], _site.Library.Imports);
        Assert.Null((await _site.SyncedRecordsAsync()).Single(r => r.RecordId == broken.Id).LastError);
        Assert.DoesNotContain(fine.Id, _site.Library.Imports);
    }

    [Fact]
    public async Task TwoServersSyncingFromEachOtherDontPassTheSameChangeBackAndForth()
    {
        var fromHq = _hq.Library.Add(ImportExportRecordType.Game, "From HQ");
        var fromSite = _site.Library.Add(ImportExportRecordType.Game, "From Site");

        var hqAtSite = _site.AddRemote(_hq);
        var siteAtHq = _hq.AddRemote(_site);

        for (var round = 0; round < 3; round++)
        {
            await _site.SyncFromAsync(hqAtSite);
            await _hq.SyncFromAsync(siteAtHq);
        }

        Assert.Equal([fromHq.Id], _site.Library.Imports);
        Assert.Equal([fromSite.Id], _hq.Library.Imports);

        // A change made at the site after it synced is the site's own, and goes back
        _site.Library.Touch(fromHq.Id);
        _hq.Library.Imports.Clear();

        await _hq.SyncFromAsync(siteAtHq);

        Assert.Equal([fromHq.Id], _hq.Library.Imports);
    }

    [Fact]
    public async Task ARecordComesToAThirdServerThroughASecondButNotBackToWhereItStarted()
    {
        await using var edge = await SyncServer.StartAsync("edge", _network);

        var game = _hq.Library.Add(ImportExportRecordType.Game, "Game");

        var hqAtSite = _site.AddRemote(_hq);
        var siteAtEdge = edge.AddRemote(_site);
        var edgeAtHq = _hq.AddRemote(edge);

        await _site.SyncFromAsync(hqAtSite);
        await edge.SyncFromAsync(siteAtEdge);
        await _hq.SyncFromAsync(edgeAtHq);

        Assert.Equal([game.Id], edge.Library.Imports);
        Assert.Empty(_hq.Library.Imports);
    }

    [Fact]
    public async Task ARemoteThatIsThisServerIsRefused()
    {
        _site.Settings.Update(s => s.InstanceId = _hq.Remotes.InstanceId);

        var run = await _site.SyncFromAsync(_site.AddRemote(_hq));

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Contains("instance id", run.Error);
    }

    [Fact]
    public async Task AnUnreachableRemoteFailsTheRun()
    {
        var run = await _site.SyncFromAsync(_site.AddRemote(_hq, r => r.Address = "http://nowhere.test"));

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Contains("could not be reached", run.Error);
    }

    [Fact]
    public async Task ACancelledSyncStopsAndSaysSo()
    {
        _hq.Library.Add(ImportExportRecordType.Game, "Game");

        using var cancelled = new CancellationTokenSource();

        await cancelled.CancelAsync();

        var run = await _site.SyncFromAsync(_site.AddRemote(_hq), cancelled.Token);

        Assert.Equal(SyncRunStatus.Cancelled, run.Status);
        Assert.NotNull(run.FinishedOn);
    }
}
