using System.ComponentModel.DataAnnotations;

namespace LANCommander.Plugins.Sync.Remotes;

/// <summary>The plugin's settings, kept by the server in the plugin's Settings.yaml.</summary>
public sealed class SyncSettings
{
    /// <summary>
    /// Identifies this server to the servers it syncs with, so a record that came from here isn't
    /// brought back. Made on first use; a server copied from another needs a new one.
    /// </summary>
    public Guid InstanceId { get; set; }

    /// <summary>The servers this one syncs from.</summary>
    public List<RemoteServer> Remotes { get; set; } = [];
}

/// <summary>A LANCommander server this one brings games, redistributables and tools over from.</summary>
public sealed class RemoteServer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    /// <summary>The remote server's address, e.g. <c>http://lancommander-hq:1337</c>.</summary>
    public string Address { get; set; } = "";

    /// <summary>
    /// A personal access token made on the remote server by an administrator, limited to the Sync
    /// plugin.
    /// </summary>
    public string AccessToken { get; set; } = "";

    /// <summary>Whether the server is synced on its schedule. It can always be synced by hand.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When it's synced, as a cron expression read in the server's time zone; every hour by default.
    /// Run by the server's background jobs, so it's on their dashboard.
    /// </summary>
    public string Schedule { get; set; } = SyncSchedules.Hourly;

    /// <summary>The archive storage location imported archives go to, or null for the default.</summary>
    public Guid? StorageLocationId { get; set; }

    public GameSyncMode Games { get; set; } = GameSyncMode.All;

    /// <summary>The remote server's collections whose games are synced, when <see cref="Games"/> is <see cref="GameSyncMode.Collections"/>.</summary>
    public List<Guid> CollectionIds { get; set; } = [];

    public DependencySyncMode Redistributables { get; set; } = DependencySyncMode.UsedByGames;

    public DependencySyncMode Tools { get; set; } = DependencySyncMode.UsedByGames;

    /// <summary>Whether games' keys come with them. Off by default, since keys are often licensed per site.</summary>
    public bool IncludeKeys { get; set; }

    public RemoteServer Clone() => new()
    {
        Id = Id,
        Name = Name,
        Address = Address,
        AccessToken = AccessToken,
        Enabled = Enabled,
        Schedule = Schedule,
        StorageLocationId = StorageLocationId,
        Games = Games,
        CollectionIds = [.. CollectionIds],
        Redistributables = Redistributables,
        Tools = Tools,
        IncludeKeys = IncludeKeys,
    };
}

public enum GameSyncMode
{
    [Display(Name = "None")]
    None,
    [Display(Name = "All games")]
    All,
    [Display(Name = "Games in chosen collections")]
    Collections,
}

public enum DependencySyncMode
{
    [Display(Name = "None")]
    None,
    [Display(Name = "Only Used By Games")]
    UsedByGames,
    [Display(Name = "All")]
    All,
}

/// <summary>Schedules offered when editing a remote server; any cron expression will do.</summary>
public static class SyncSchedules
{
    public const string EveryFifteenMinutes = "*/15 * * * *";
    public const string Hourly = "0 * * * *";
    public const string EverySixHours = "0 */6 * * *";
    public const string Nightly = "0 2 * * *";
    public const string Weekly = "0 2 * * 0";

    public static IReadOnlyList<(string Cron, string Label)> Presets { get; } =
    [
        (EveryFifteenMinutes, "Every 15 minutes"),
        (Hourly, "Every hour"),
        (EverySixHours, "Every 6 hours"),
        (Nightly, "Every night at 2 AM"),
        (Weekly, "Every Sunday at 2 AM"),
    ];

    /// <summary>How the schedule reads to an administrator: a preset's name, or the expression itself.</summary>
    public static string Describe(string cron) =>
        Presets.FirstOrDefault(p => p.Cron == cron.Trim()).Label ?? cron;
}
