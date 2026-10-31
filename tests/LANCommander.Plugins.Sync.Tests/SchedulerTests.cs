using LANCommander.Plugins.Sync.Data;
using LANCommander.Plugins.Sync.Remotes;
using LANCommander.Plugins.Sync.Syncing;
using LANCommander.SDK.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LANCommander.Plugins.Sync.Tests;

public sealed class SchedulerTests : IAsyncLifetime
{
    private readonly Network _network = new();
    private SyncServer _hq = default!;
    private SyncServer _site = default!;

    public async Task InitializeAsync()
    {
        _hq = await SyncServer.StartAsync("hq", _network);
        _site = await SyncServer.StartAsync("site", _network);

        await Scheduler.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await Scheduler.StopAsync(CancellationToken.None);
        await _hq.DisposeAsync();
        await _site.DisposeAsync();
    }

    SyncScheduler Scheduler => _site.Services.GetRequiredService<SyncScheduler>();
    SyncJobs SyncJobs => _site.Services.GetRequiredService<SyncJobs>();
    FakeJobScheduler Jobs => _site.Services.GetRequiredService<FakeJobScheduler>();

    [Fact]
    public async Task ARemoteThatSyncsAutomaticallyGetsAJobThatSyncsIt()
    {
        var game = _hq.Library.Add(ImportExportRecordType.Game, "Game");
        var remote = _site.AddRemote(_hq, r => r.Schedule = SyncSchedules.Nightly);

        SyncJobs.Start();

        var job = Assert.Single(Jobs.Jobs);

        Assert.Equal(SyncJobs.JobId(remote.Id), job.Key);
        Assert.Equal(SyncSchedules.Nightly, job.Value.Cron);
        Assert.NotNull(SyncJobs.NextSync(remote.Id));

        // The job waits for the sync, so the server's dashboard shows how long it took
        await Jobs.RunAsync(job.Key);

        Assert.Equal([game.Id], _site.Library.Imports);
        Assert.Null(Scheduler.Running);
    }

    [Fact]
    public void JobsFollowChangesToRemoteServers()
    {
        SyncJobs.Start();

        var remote = _site.AddRemote(_hq);
        var jobId = SyncJobs.JobId(remote.Id);

        Assert.Equal(SyncSchedules.Hourly, Jobs.Jobs[jobId].Cron);

        remote.Schedule = SyncSchedules.EverySixHours;
        _site.Remotes.Save(remote);

        Assert.Equal(SyncSchedules.EverySixHours, Jobs.Jobs[jobId].Cron);

        remote.Enabled = false;
        _site.Remotes.Save(remote);

        Assert.Empty(Jobs.Jobs);

        remote.Enabled = true;
        _site.Remotes.Save(remote);
        _site.Remotes.Remove(remote.Id);

        Assert.Empty(Jobs.Jobs);
    }

    [Fact]
    public void AScheduleThatIsntOneIsRefused()
    {
        var remote = new RemoteServer { Name = "HQ", Address = _hq.Address, AccessToken = "lc_pat_test", Schedule = "every hour" };

        var exception = Assert.Throws<ArgumentException>(() => _site.Remotes.Save(remote));

        Assert.Contains("isn't a schedule", exception.Message);
        Assert.Empty(_site.Remotes.All);
    }

    [Fact]
    public async Task AJobForARemoteSwitchedOffSinceDoesNothing()
    {
        _hq.Library.Add(ImportExportRecordType.Game, "Game");

        var remote = _site.AddRemote(_hq);

        SyncJobs.Start();

        var jobId = SyncJobs.JobId(remote.Id);
        var job = Jobs.Jobs[jobId];

        // Turned off in the settings file while the job was already due
        _site.Settings.Current.Remotes.Single().Enabled = false;

        await Jobs.RunAsync(jobId);

        Assert.Empty(_site.Library.Imports);
        Assert.Equal(job, Jobs.Jobs[jobId]);
    }

    [Fact]
    public async Task ASyncIsOnlyQueuedOnceAndAJobWaitsForTheOneQueued()
    {
        var game = _hq.Library.Add(ImportExportRecordType.Game, "Game");
        var remote = _site.AddRemote(_hq);

        Assert.True(Scheduler.Request(remote.Id));
        Assert.False(Scheduler.Request(remote.Id));

        await Scheduler.RequestAndWaitAsync(remote.Id, SyncTrigger.Scheduled, CancellationToken.None);

        Assert.Equal([game.Id], _site.Library.Imports);
    }

    [Fact]
    public async Task ARunARestartInterruptedIsMarkedFailedBeforeTheNextSync()
    {
        var remote = _site.AddRemote(_hq, r => r.Enabled = false);
        var interrupted = Guid.NewGuid();

        await using (var scope = _site.Services.CreateAsyncScope())
        {
            var data = scope.ServiceProvider.GetRequiredService<SyncContext>();

            data.Runs.Add(new SyncRun { Id = interrupted, RemoteId = remote.Id, Status = SyncRunStatus.Running, StartedOn = DateTime.UtcNow.AddHours(-1) });
            await data.SaveChangesAsync();
        }

        await Scheduler.RequestAndWaitAsync(remote.Id, SyncTrigger.Manual, CancellationToken.None);

        await using (var scope = _site.Services.CreateAsyncScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<SyncContext>().Runs.AsNoTracking().SingleAsync(r => r.Id == interrupted);

            Assert.Equal(SyncRunStatus.Failed, run.Status);
            Assert.Contains("stopped", run.Error);
        }
    }
}
