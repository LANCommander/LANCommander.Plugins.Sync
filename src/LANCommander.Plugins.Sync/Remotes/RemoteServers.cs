using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;

namespace LANCommander.Plugins.Sync.Remotes;

/// <summary>The remote servers this one syncs from, and this server's own sync identity.</summary>
public sealed class RemoteServers(IPluginSettings<SyncSettings> settings, IRecurringJobScheduler jobs)
{
    private readonly Lock _lock = new();

    /// <summary>Raised when a remote server is added, changed or removed.</summary>
    public event Action? Changed;

    /// <summary>This server's instance id, made on first use.</summary>
    public Guid InstanceId
    {
        get
        {
            if (settings.Current.InstanceId != Guid.Empty)
                return settings.Current.InstanceId;

            lock (_lock)
            {
                if (settings.Current.InstanceId == Guid.Empty)
                    settings.Update(s => s.InstanceId = Guid.NewGuid());

                return settings.Current.InstanceId;
            }
        }
    }

    /// <summary>Copies of the remote servers, ordered by name.</summary>
    public IReadOnlyList<RemoteServer> All => settings.Current.Remotes
        .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(r => r.Clone())
        .ToList();

    /// <summary>A copy of the remote server, or null.</summary>
    public RemoteServer? Find(Guid id) => settings.Current.Remotes.FirstOrDefault(r => r.Id == id)?.Clone();

    /// <summary>Adds the remote server, or replaces the one with its id.</summary>
    /// <exception cref="ArgumentException">It's missing something it needs.</exception>
    public void Save(RemoteServer remote)
    {
        Validate(remote);

        if (!jobs.IsValidCronExpression(remote.Schedule))
            throw new ArgumentException($"\"{remote.Schedule}\" isn't a schedule. Use a cron expression such as {SyncSchedules.Hourly} for every hour.", nameof(remote));

        var saved = remote.Clone();

        saved.Name = saved.Name.Trim();
        saved.Address = NormalizeAddress(saved.Address);
        saved.AccessToken = saved.AccessToken.Trim();
        saved.Schedule = saved.Schedule.Trim();
        saved.CollectionIds = saved.CollectionIds.Distinct().ToList();

        settings.Update(s =>
        {
            s.Remotes.RemoveAll(r => r.Id == saved.Id);
            s.Remotes.Add(saved);
        });

        Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        settings.Update(s => s.Remotes.RemoveAll(r => r.Id == id));

        Changed?.Invoke();
    }

    /// <summary>The address without a trailing slash, which every request path is added to.</summary>
    /// <summary>What the host's personal access tokens start with.</summary>
    public const string AccessTokenPrefix = "lc_pat_";

    public static string NormalizeAddress(string address) => address.Trim().TrimEnd('/');

    internal static void Validate(RemoteServer remote)
    {
        if (string.IsNullOrWhiteSpace(remote.Name))
            throw new ArgumentException("A remote server needs a name.", nameof(remote));

        if (!Uri.TryCreate(NormalizeAddress(remote.Address ?? ""), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"\"{remote.Address}\" isn't a server address, such as http://lancommander:1337.", nameof(remote));

        if (string.IsNullOrWhiteSpace(remote.AccessToken))
            throw new ArgumentException("A remote server needs an access token, made on that server under Access Tokens in the profile menu.", nameof(remote));

        // Catches a browser filling a saved password into the field, which the remote would otherwise
        // turn away with nothing more than a redirect to its login page
        if (!remote.AccessToken.Trim().StartsWith(AccessTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException($"That isn't an access token: they start with {AccessTokenPrefix}. Make one on the remote server under Access Tokens in the profile menu.", nameof(remote));

        if (string.IsNullOrWhiteSpace(remote.Schedule))
            throw new ArgumentException("A remote server needs a schedule, even if it's only synced by hand.", nameof(remote));

        if (remote.Games == GameSyncMode.Collections && remote.CollectionIds.Count == 0)
            throw new ArgumentException("Choose at least one collection to sync games from.", nameof(remote));
    }
}
