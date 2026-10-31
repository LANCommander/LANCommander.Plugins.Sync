# LANCommander.Plugins.Sync

Syncs games, redistributables and tools between [LANCommander](https://github.com/LANCommander/LANCommander)
servers, for example from a central library to the servers at each site.

A server lists the remote servers it syncs from. On each sync it asks a remote server which of its
records the filters let through, and imports those that are new or have changed there since the last
sync. A record travels as an LCX package: the remote server exports it with the same export as its
**Export** button, and this server imports it with the same import as its **Import** button. Records
keep their ids, so a record synced again updates the copy it made the first time.

## Using it

1. Install the plugin from **Settings → Plugins** on every server taking part, and restart them.
2. On the remote server, an administrator makes an access token under **Access Tokens** in the profile
   menu, allowed the **Server Sync** plugin. Tokens belong to their maker, so it must be an
   administrator's.
3. On the server that syncs, add the remote server under **Settings → Server Sync**: its address, the
   token, and what to sync. **Test Connection** checks the token and loads the remote server's
   collections.
4. Save. A remote server that syncs automatically does so on its schedule; any of them can be synced
   by hand with **Sync Now**. A remote server's page lists its runs and every record it has brought
   over, failures first.

Syncs run one at a time. To sync both ways, add each server on the other.

### Schedules

Each remote server's schedule is a cron expression, read in the server's time zone: every hour by
default, one of a few presets (every 15 minutes, every 6 hours, nightly at 2 AM, weekly), or any
expression, such as `30 1 * * 1-5` for 1:30 AM on weekdays. Schedules run as recurring jobs on the
server's background job processor, through `IRecurringJobScheduler`, so they're on its dashboard as
`plugin:lancommander.sync.<remote id>`; each job waits for its sync to finish. The processor keeps no
schedules across restarts, so the plugin adds them again whenever the server starts.

### What is synced

| Setting | Choices |
| --- | --- |
| Games | None, all of them, or those in chosen collections on the remote server. An add-on brings its base game, and a game its add-ons. |
| Redistributables, Tools | None, those the synced games use, or all of them. They're imported before the games that use them. |
| Keys | Off by default, since keys are often licensed to one site. |
| Archive storage | Which of this server's archive storage locations synced archives go to. |

Saves and play sessions belong to players and never travel.

### What counts as a change

The remote server fingerprints each record from its own timestamp and those of everything a package of
it carries: archives (with their sizes), media, scripts, actions, save paths, keys and so on. A record
is imported when its fingerprint differs from the one it had when it was last imported, or when it has
been deleted here since. Keys count by id alone, so a player claiming one isn't a change.

Archives this server already holds, with the same size, are left out of the package, so changing a
game's description doesn't download its files again.

### Servers syncing from each other

Every server has a sync instance id, shown at the foot of the Server Sync page. A server remembers the
origin of everything it imports, and passes it on, so a record never comes back to the server it
started on unless it was changed somewhere in between. Two servers can sync from each other, or several
in a ring, without passing the same change around forever. A server copied from another has the same
instance id, and the two refuse to sync; remove `InstanceId` from the plugin's `Settings.yaml` on one of
them and restart it.

## Limitations

- Imports add and update, as the server's import does. A record deleted on the remote server stays
  here, as does a script, action or other child record removed from a record that's still synced.
- When both servers change the same record, the one that syncs last wins.
- The server's import matches a game by title as well as id, so a game with the same title made
  separately on each server is updated in place rather than duplicated.
- A sync that's due while another is running waits for it, and one that's still running when its next
  time comes isn't started twice.
- Each sync describes every record its filters let through to fingerprint it, which reads each record
  and its children from the remote server's database. Large libraries make for a slow first step;
  lengthen the interval to suit.
- Servers aren't synced; their configuration is too tied to the machine they run on.

## How it works

Server Sync adds four endpoints to every server it's installed on, under `/api/Plugins/Sync`, all for
administrators only:

| Endpoint | Answers |
| --- | --- |
| `GET Info` | The server's instance id and protocol version |
| `GET Collections` | Its collections, for the filter |
| `POST Catalog` | The records the filters let through, with their fingerprints, origins and archives |
| `POST Export` | An LCX package of one record, leaving out the child records named |

Packages are written by the server's `IPackageExporter` and read by its `IPackageImporter`, and syncs
are scheduled through its `IRecurringJobScheduler`, all from `LANCommander.Server.Plugins`. What each server has synced, and each run, are kept in the server's
database, in `Plugin_Sync_Records` and `Plugin_Sync_Runs`; the remote servers are in the plugin's
`Settings.yaml`.

## Building

The plugin builds against the LANCommander server's `LANCommander.SDK`, `LANCommander.Server.Plugins`
and `LANCommander.Server.UI` packages, of the version in `Directory.Packages.props`. To build against an
unreleased server, pack those projects into `artifacts/packages` (used as a package source when it
exists), and pass the version:

```
dotnet pack ../LANCommander/LANCommander.Server.Plugins -c Release -p:Version=2.2.0-local.1 -o artifacts/packages
# and the same for LANCommander.SDK and LANCommander.Server.UI
dotnet build -p:LANCommanderVersion=2.2.0-local.1
```

`dotnet build -t:Deploy` copies the plugin into the Plugins folder of a server run from a LANCommander
checkout beside this repository; `dotnet pack` makes the package the Plugins page installs.

### Projects

| Project | Holds |
| --- | --- |
| `LANCommander.Plugins.Sync` | The plugin: endpoints, sync runner and scheduler, pages |
| `LANCommander.Plugins.Sync.Data` | `SyncContext` and its entities |
| `LANCommander.Plugins.Sync.Migrations.{Sqlite,MySql,PostgreSql}` | The context's migrations, one assembly per engine |

The context is in an assembly of its own so the migrations projects can reference it while the plugin
references them. To change the model, add a migration to each engine with the same name, from each
migrations project's folder:

```
dotnet ef migrations add <Name>
```

The design-time factories need no database server; `LANCommanderVersion` can be set as an environment
variable for `dotnet ef`, which doesn't pass MSBuild properties through.
