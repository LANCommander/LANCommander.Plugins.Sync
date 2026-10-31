using Bunit;
using LANCommander.Plugins.Sync.Components;
using LANCommander.Plugins.Sync.Data;
using LANCommander.Plugins.Sync.Pages;
using LANCommander.Plugins.Sync.Remotes;
using LANCommander.Plugins.Sync.Syncing;
using LANCommander.SDK.Enums;
using LANCommander.Server.UI.Controls;
using LANCommander.Server.UI.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LANCommander.Plugins.Sync.Tests;

public sealed class PageTests : BunitContext, IAsyncLifetime
{
    private readonly Network _network = new();
    private readonly FakeLibrary _library = new();
    private readonly MemorySettings<SyncSettings> _settings = new();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private SyncServer _hq = default!;

    public PageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddLANCommanderServerUI();
        Services.AddLogging();

        SyncServer.AddServices(Services, _library, _settings, _connection, _network);
    }

    public async Task InitializeAsync()
    {
        _hq = await SyncServer.StartAsync("hq", _network);

        await _connection.OpenAsync();

        await using var data = await Services.GetRequiredService<IDbContextFactory<SyncContext>>().CreateDbContextAsync();

        await data.Database.EnsureCreatedAsync();
    }

    public new async Task DisposeAsync()
    {
        await _hq.DisposeAsync();
        await _connection.DisposeAsync();
    }

    RemoteServers Remotes => Services.GetRequiredService<RemoteServers>();

    [Fact]
    public void WithoutRemoteServersThePageSaysHowToStart()
    {
        var page = Render<RemotesPage>();

        Assert.Contains("No remote servers", page.Markup);
        Assert.Contains(Remotes.InstanceId.ToString(), page.Markup);
    }

    [Fact]
    public async Task AnAdministratorAddsARemoteServerFilteredByCollection()
    {
        var lanParty = _hq.Library.AddCollection("LAN Party");

        _hq.Library.AddCollection("Retro");

        var page = Render<RemotesPage>();

        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Add Remote Server").ClickAsync(new());

        var editor = page.FindComponent<RemoteEditor>();

        await editor.InvokeAsync(async () =>
        {
            var inputs = editor.FindComponents<TextInput>();

            await inputs[0].Instance.ValueChanged.InvokeAsync("HQ");
            await inputs[1].Instance.ValueChanged.InvokeAsync(_hq.Address + "/");
            await editor.FindComponent<PasswordInput>().Instance.ValueChanged.InvokeAsync("lc_pat_test");
        });

        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Test Connection").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Contains("with 2 collection(s)", page.Markup));

        await editor.InvokeAsync(() => editor.FindComponents<EnumSelect<GameSyncMode>>().Single().Instance.ValueChanged.InvokeAsync(GameSyncMode.Collections));

        page.WaitForAssertion(() => Assert.Contains("Collections", page.Markup));

        await editor.InvokeAsync(() => editor.FindComponents<Select<Guid>>().Single().Instance.ValuesChanged.InvokeAsync([lanParty.Id]));

        await page.Find("form").SubmitAsync();

        var saved = Assert.Single(_settings.Current.Remotes);

        Assert.Equal("HQ", saved.Name);
        Assert.Equal(_hq.Address, saved.Address);
        Assert.Equal(GameSyncMode.Collections, saved.Games);
        Assert.Equal([lanParty.Id], saved.CollectionIds);

        page.WaitForAssertion(() => Assert.Contains("Games in 1 collection(s)", page.Markup));
        Assert.Contains("Every hour", page.Markup);
        Assert.Equal(SyncSchedules.Hourly, saved.Schedule);
    }

    [Fact]
    public async Task SavingWithoutACollectionToSyncFromIsRefused()
    {
        var page = Render<RemotesPage>();

        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Add Remote Server").ClickAsync(new());

        var editor = page.FindComponent<RemoteEditor>();

        editor.Instance.Remote.Name = "HQ";
        editor.Instance.Remote.Address = _hq.Address;
        editor.Instance.Remote.AccessToken = "lc_pat_test";
        editor.Instance.Remote.Games = GameSyncMode.Collections;

        await page.Find("form").SubmitAsync();

        Assert.Empty(_settings.Current.Remotes);
    }

    [Fact]
    public async Task ARemoteServersPageShowsItsRunsAndRecords()
    {
        var game = _hq.Library.Add(ImportExportRecordType.Game, "Unreal Tournament");
        var broken = _hq.Library.Add(ImportExportRecordType.Game, "Broken Game");

        var remote = new RemoteServer { Name = "HQ", Address = _hq.Address, AccessToken = "lc_pat_test", Enabled = false };

        Remotes.Save(remote);
        _library.FailImports.Add(broken.Id);

        await using (var scope = Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<SyncRunner>().RunAsync(remote, new SyncProgress(remote.Id, remote.Name, SyncTrigger.Manual), CancellationToken.None);

        var page = Render<RemotePage>(parameters => parameters.Add(p => p.RemoteId, remote.Id));

        page.WaitForAssertion(() => Assert.Contains("Completed with errors", page.Markup));

        Assert.Contains("Records (2)", page.Markup);

        var list = Render<RemotesPage>();

        list.WaitForAssertion(() => Assert.Contains("Completed with errors", list.Markup));
        Assert.Contains("By hand", list.Markup);
        Assert.Contains(game.Name, _library.Records[game.Id].Name);
    }

    [Fact]
    public void AnUnknownRemoteServerIsSaidToBeMissing()
    {
        var page = Render<RemotePage>(parameters => parameters.Add(p => p.RemoteId, Guid.NewGuid()));

        Assert.Contains("There is no such remote server", page.Markup);
    }
}
