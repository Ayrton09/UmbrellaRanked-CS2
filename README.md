<h1 align="center">Umbrella Ranked</h1>

<p align="center">
  A native <a href="https://docs.cssharp.dev/">CounterStrikeSharp</a> ranking system for CS2:
  points, KDA, playtime and per-weapon stats, with an in-game WASD menu and full localization.
</p>

<p align="center">
  <a href="https://github.com/Ayrton09/UmbrellaRanked-CS2/actions/workflows/build.yml"><img alt="Build status" src="https://github.com/Ayrton09/UmbrellaRanked-CS2/actions/workflows/build.yml/badge.svg"></a>
  <a href="https://github.com/Ayrton09/UmbrellaRanked-CS2/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/Ayrton09/UmbrellaRanked-CS2"></a>
  <a href="https://github.com/Ayrton09/UmbrellaRanked-CS2/releases"><img alt="Total downloads" src="https://img.shields.io/github/downloads/Ayrton09/UmbrellaRanked-CS2/total"></a>
  <a href="https://github.com/roflmuffin/CounterStrikeSharp"><img alt="CounterStrikeSharp 1.0.380 or newer" src="https://img.shields.io/badge/CounterStrikeSharp-1.0.380%2B-orange"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
  <img alt="Database: MySQL or SQLite" src="https://img.shields.io/badge/database-MySQL%20%7C%20SQLite-336791">
  <img alt="Counter-Strike 2" src="https://img.shields.io/badge/game-CS2-f5a623">
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/github/license/Ayrton09/UmbrellaRanked-CS2"></a>
  <a href="https://github.com/Ayrton09/UmbrellaRanked-CS2/commits"><img alt="Last commit" src="https://img.shields.io/github/last-commit/Ayrton09/UmbrellaRanked-CS2"></a>
</p>

---

## Contents

- [What it does](#what-it-does)
- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Updating](#updating)
- [Commands and console variable](#commands-and-console-variable)
- [Menu controls](#menu-controls)
- [Configuration](#configuration)
- [Points](#points)
- [How players are identified](#how-players-are-identified)
- [Diagnostics](#diagnostics)
- [Troubleshooting and FAQ](#troubleshooting-and-faq)
- [Database schema](#database-schema)
- [Building from source](#building-from-source)
- [License](#license)

## What it does

Umbrella Ranked keeps a persistent ranking for the real players of a CS2 server. Every kill, death, assist, headshot, MVP, round and match is tracked in memory while the player is connected and saved to a database. Players check their position with `!rank` and browse the leaderboards (points or KDA, playtime, per-weapon) in an in-game WASD menu, with no extra client files. Bots are never ranked.

## Features

|  | |
| --- | --- |
| **Stats** | Kills, deaths, assists, headshots, MVPs, KDA, points, playtime, rounds and matches won or lost, rounds per side (CT and T), and per-weapon kills and headshots |
| **Ranking** | Two modes, `Points` or `Kda`, with a full tie-breaker chain so positions are stable. Players appear in the lists once they reach `MinimumKillsRequired` |
| **Web panel friendly** | Players are keyed by SteamID64, the format web panels and other plugins use. Older Steam2 rows are converted automatically |
| **Storage** | MySQL / MariaDB or SQLite, chosen explicitly through `DatabaseMode`. No silent fallback: a misconfigured backend refuses to load instead of quietly losing data |
| **Menus** | Built-in WASD menu with pagination, per-entry detail pages and a reset confirmation prompt |
| **Announcements** | Join announcements for top-ranked players, with an optional Top #1 sound |
| **Durability** | Periodic autosave plus saves on disconnect, map end and plugin unload. Failed loads are retried automatically |
| **Localization** | English, Spanish, Portuguese, Russian and Chinese (`zh` and `zh-CN`) through CounterStrikeSharp `lang/*.json` |
| **Control** | Runtime toggle through `css_rank_enabled`, and map patterns that pause competitive ranking while playtime keeps counting |

## Requirements

- **CounterStrikeSharp `1.0.380`** or newer (with Metamod:Source). The plugin API version is the CounterStrikeSharp build number, so `1.0.380` reports `380`
- **.NET 10** runtime
- **MySQL 5.7+ / MariaDB**, or nothing extra if you use SQLite
- **Linux servers using SQLite:** the bundled native SQLite library asks for glibc 2.28 at most, so it runs on glibc 2.31 and newer. That is satisfied by the Steam Runtime 3 "sniper" container (Debian 11, glibc 2.31) that CS2 dedicated servers run in. Older plugin builds shipped a library that needed glibc 2.34 and failed there with `GLIBC_2.3x not found`

## Installation

1. Download the release archive and extract it into your server's `game/csgo/` directory. It contains the full `addons/` tree.
2. Edit `addons/counterstrikesharp/configs/plugins/umbrellaranked/umbrellaranked.json` (see [Configuration](#configuration)).
3. Restart the server, or load the plugin with `css_plugins load umbrellaranked`.

Tables, columns and indexes are created automatically on first load. The archive contains two config files, both copies of the MySQL sample: `umbrellaranked.json` is the one the plugin reads, `umbrellaranked.mysql.sample.json` is a spare reference.

### Fastest path: SQLite

Nothing to install and no server to run. Set `DatabaseMode` to `Sqlite` and you are done; the database file is created at `data/umbrella_ranked.sqlite` inside the plugin folder.

```json
"DatabaseMode": "Sqlite"
```

The files in `samples/` list every setting with its default; only `DatabaseMode` has to change.

### MySQL / MariaDB

1. Create an empty database and a user that can create and alter tables in it.
2. Fill in the `MySql` block of the config:

```json
"DatabaseMode": "MySql",
"MySql": {
  "Host": "127.0.0.1",
  "Port": 3306,
  "Database": "umbrella_ranked",
  "Username": "user",
  "Password": "your-password"
}
```

3. Start the server. The log must show `Umbrella Ranked <version> loaded with the MySql backend`.

MySQL mode refuses to load when `Host`, `Database` or `Username` is empty. Use it when several servers must share one ranking.

### Files that must ship next to the plugin DLL

```text
addons/counterstrikesharp/plugins/umbrellaranked/
├── umbrellaranked.dll
├── umbrellaranked.deps.json
├── Dapper.dll
├── MySqlConnector.dll
├── Microsoft.Extensions.DependencyInjection.Abstractions.dll
├── Microsoft.Extensions.Logging.Abstractions.dll
├── lang/                 # required
└── sqlite/               # required only when DatabaseMode = Sqlite
```

## Updating

Replace the plugin folder contents with the new release (keep `data/` if you use SQLite) and restart or reload the plugin. The plugin itself never rewrites your config file, but the release archive ships a default `configs/plugins/umbrellaranked/umbrellaranked.json`.

> [!WARNING]
> When updating, extract only `addons/counterstrikesharp/plugins/umbrellaranked/`, or skip `configs/` when the archive asks to overwrite. Extracting the whole archive over an existing install replaces your config, database password included.

> [!IMPORTANT]
> Always replace `lang/` too. Missing keys fall back to the raw key name in chat.

**From 1.2.x to 1.3.0**

- No database, `lang/` or dependency changes.
- Four new point settings, all `0` (off) by default: `HostagePickUp`, `HostageKillPenalty`, `C4PickUp` and `C4DropPenalty`. Add them to your config to turn them on; see [Points](#points).

**From 1.1.x to 1.2.0**

- Requires CounterStrikeSharp `1.0.380` or newer.
- New `lang/` keys (`top.mode.points`, `top.mode.kda`): replace the folder.
- SQLite on Linux: the bundled native library now needs glibc 2.28 at most, so it runs inside the Steam Runtime 3 container.
- Out-of-range config values are clamped on load with a warning in the log; see the limits in [Configuration](#configuration).
- `Top1Sound` in `SoundEvent` mode no longer prepends `sound/` to the path; see [Top #1 sound](#top-1-sound).
- On servers that do not authenticate players through Steam, see [How players are identified](#how-players-are-identified).

**From 1.0.x to 1.1.x**

- The first start converts every Steam2 key (`STEAM_1:0:12345`) to SteamID64, in place and with the weapon stats, in one transaction. The log says how many players were migrated.
- **If several servers share one database, update all of them together.** A server still on an older version keeps writing Steam2 rows; those are left alone, with a warning in the log, when the player already has a SteamID64 row.
- New columns start at `0`, so the headshot percentage of players with older kills starts low and catches up as they play.

## Commands and console variable

### Players

Each command works as `!cmd`, `/cmd` (the `css_` console command) and as a plain chat word.

| Command | Description |
| --- | --- |
| `rank` | Show your position, points, K/D/A, KDA, playtime, headshot percentage and MVPs |
| `top` | Open the points or KDA leaderboard |
| `toptime` | Open the playtime leaderboard |
| `topweapons` · `toparmas` | Open the per-weapon leaderboard menu |
| `resetrank` · `rrank` | Reset your own stats, with a confirmation prompt |

`rank`, `top`, `topweapons` and `resetrank` answer "competitive ranking is disabled" while ranking is paused (see [Troubleshooting](#troubleshooting-and-faq)); `toptime` always works.

### Admins

Both require the `@css/root` permission and also work from the server console.

| Command | Description |
| --- | --- |
| `css_rank_status` | Print backend, session, cache and autosave diagnostics |
| `css_rank_prunenow` | Run the inactive-player prune immediately. Tells you when `PruneInactiveDays` is `0` instead of doing nothing silently |

### Console variable

```cfg
css_rank_enabled 1
```

Setting it to `0` immediately pauses competitive rank tracking and closes the rank and weapon menus. Playtime tracking and `toptime` keep working.

## Menu controls

| Key | Action |
| --- | --- |
| `W` · `S` | Move selection |
| `A` · `D` | Previous / next page |
| `E` | Select |
| `R` | Back |
| `Jump` · `Duck` | Close |

Menus close automatically after 45 seconds of inactivity, and on death or team change.

## Configuration

Start from [`samples/UmbrellaRanked.mysql.sample.json`](samples/UmbrellaRanked.mysql.sample.json), which lists every setting. Out-of-range values are clamped on load and every adjustment is written to the server log as a warning. The "Limits" column lists what the plugin accepts.

### General

| Setting | Default | Limits | Description |
| --- | --- | --- | --- |
| `Enabled` | `true` | | Master switch for competitive ranking |
| `DatabaseMode` | `MySql` | `MySql` or `Sqlite` | Storage backend |
| `RankingMode` | `Points` | `Points` or `Kda` | How the leaderboards are ordered |
| `MinimumKillsRequired` | `100` | at least `100` | Kills needed to appear in ranked lists and to get a position |
| `MinimumPlayersForStats` | `4` | at least `1` | Real players connected before competitive stats count |
| `DisabledRankMapPatterns` | surf / mg / bhop / jb / dr / deathrun | `*` wildcard | Map patterns where competitive ranking pauses. Matched against the full map name and its last path segment, case-insensitive |
| `CommandCooldownSeconds` | `3.0` | at least `0` | Per-player anti-spam delay |
| `AllowResetRank` | `true` | | Allow players to reset their own stats |
| `ResetRankCooldownDays` | `30` | at least `0` (`0` disables) | Cooldown between self-resets |
| `TopAnnouncementThreshold` | `5` | `0` to `500` (`0` disables) | Announce joining players ranked within this position |
| `LeaderboardLimit` | `50` | `1` to `500` | Rows fetched for the leaderboards (values of `0` or less reset to `50`) |
| `TopCacheSeconds` | `20.0` | `0` to `3600` (`0` disables caching) | Leaderboard cache lifetime |

### Persistence and maintenance

| Setting | Default | Limits | Description |
| --- | --- | --- | --- |
| `AutosaveIntervalSeconds` | `120.0` | `0` or at least `10` | Periodic save interval. `0` disables the periodic save (disconnect, map end and unload still save) |
| `PruneInactiveDays` | `35` | `0` or at least `35` | Delete players unseen for this many days. `0` disables pruning |
| `PruneOnStartup` | `false` | | Run a prune when the plugin loads |
| `PruneCheckIntervalHours` | `6.0` | `0` or at least `0.1` | Scheduled prune interval. `0` disables the schedule |

Pruning never touches players who are currently connected, and clears the leaderboard cache when it removes rows.

### Database

MySQL requires `Host`, `Database` and `Username`, otherwise the plugin refuses to load.

```json
"MySql": {
  "Host": "127.0.0.1",
  "Port": 3306,
  "Database": "umbrella_ranked",
  "Username": "user",
  "Password": "your-password",
  "ConnectionTimeoutSeconds": 15,
  "MinimumPoolSize": 0,
  "MaximumPoolSize": 50
}
```

| Setting | Default | Limits |
| --- | --- | --- |
| `ConnectionTimeoutSeconds` | `15` | `0` resets to `15` |
| `MinimumPoolSize` | `0` | cannot exceed `MaximumPoolSize` |
| `MaximumPoolSize` | `50` | `0` resets to `50` |

SQLite only needs a path, resolved relative to the plugin directory when it is not absolute.

```json
"Sqlite": {
  "FilePath": "data/umbrella_ranked.sqlite",
  "BusyTimeoutSeconds": 5,
  "UseWriteAheadLogging": true
}
```

| Setting | Default | Limits | Description |
| --- | --- | --- | --- |
| `FilePath` | `data/umbrella_ranked.sqlite` | not empty | Database file. The folder is created if missing |
| `BusyTimeoutSeconds` | `5` | `0` to `600` | How long a query waits for a locked database |
| `UseWriteAheadLogging` | `true` | | WAL journal mode |

### Top #1 sound

```json
"Top1Sound": {
  "PlaybackMode": "ClientCommand",
  "Value": "sounds/training/bell_normal.vsnd_c",
  "ResourcePath": "",
  "Volume": 0.3,
  "Pitch": 0.0
}
```

| `PlaybackMode` | Behaviour |
| --- | --- |
| `Disabled` | No sound |
| `ClientCommand` | Uses `playvol` on every player. Needs no precache. This is the default |
| `SoundEvent` | Emits a sound event from the world. The resource is precached at map load |

In `SoundEvent` mode the precached resource is `ResourcePath` exactly as written when it is set. Otherwise `Value` is precached only when it ends in `.vsnd_c` or `.vsndevts`, again exactly as written (no prefix is added, resource folders such as `sounds/` are part of the path). Any other `Value` is treated as a sound event name and has nothing to precache.

The recommended `SoundEvent` setup is a sound event file in `ResourcePath` (for example `soundevents/my_sounds.vsndevts`) and the name of an event defined in it in `Value`. A plain sound file path in `Value` gets precached, but emitting it as an event is not guaranteed to play; use `ClientCommand` for plain sound files.

## Points

```json
{
  "Kill": 2,
  "HeadshotBonus": 1,
  "KnifeKillBonus": 3,
  "TaserKillBonus": 2,
  "Assist": 1,
  "DeathPenalty": 2,
  "SuicidePenalty": 3,
  "TeamKillPenalty": 5,
  "Mvp": 1,
  "BombPlant": 2,
  "BombDefuse": 3,
  "BombExplode": 3,
  "HostageRescue": 3,
  "HostagePickUp": 0,
  "HostageKillPenalty": 0,
  "C4PickUp": 0,
  "C4DropPenalty": 0,
  "TeamWin": 1,
  "TeamLossPenalty": 1
}
```

Every value must be between `0` and `10000`; anything outside is clamped with a warning in the log. A player's total can never drop below `0`.

With the defaults above:

| Event | Attacker | Victim |
| --- | --- | --- |
| Normal kill | `+2` | `-2` |
| Headshot kill | `+3` | `-2` |
| Knife kill | `+5` | `-5` |
| Zeus / taser kill | `+4` | `-4` |
| Knife or zeus kill with a headshot | `+6` / `+5` | `-6` / `-5` |
| Suicide | none | `-5` (`DeathPenalty` + `SuicidePenalty`) |
| Teamkill | `-5` | no points lost, but the death still counts in their stats |
| Assist | `+1` | none |
| Round MVP, bomb planted, defused or exploded, hostage rescued | the matching value | none |
| Round won or lost | `+1` for each player on the winning team, `-1` for each on the losing team | |

The hostage and bomb handling values are `0` (off) by default. Set them to turn them on:

| Setting | Who | When |
| --- | --- | --- |
| `HostagePickUp` | `+` the player who picks up the hostage | Once per hostage and round, so picking the same hostage up again pays nothing |
| `HostageKillPenalty` | `-` the player who kills a hostage | Every hostage killed. Hostages only take damage with `mp_hostages_takedamage 1`, which is off by default in CS2 |
| `C4PickUp` | `+` the player who picks up the bomb | Once per player and round. The bomb handed out at spawn does not count |
| `C4DropPenalty` | `-` the player who drops the bomb | Every drop while alive. Dropping it by getting killed costs nothing |

Bomb pickups and drops during freeze time are ignored, so handing the bomb to a teammate before the round starts is free.

Bonuses stack for the attacker. For the victim, a normal or headshot kill costs just `DeathPenalty`, but a knife or zeus kill costs exactly what it gave the attacker, and that includes the `HeadshotBonus` when the kill was a headshot.

Stats are never counted during warmup, while fewer than `MinimumPlayersForStats` real players are connected, on blocked maps, or while `css_rank_enabled` is `0`.

## How players are identified

Stats are keyed by the player's **authorized** SteamID64, the one Steam has validated.

- A player who reaches the server before Steam has authorized them is loaded as soon as the authorization arrives. If it takes more than 60 seconds the log shows a warning and the player is not tracked meanwhile.
- The unverified `SteamID` field is used as a fallback **only when `sv_lan` is `1`**, because a LAN server has no Steam authentication at all. On an internet server it is never trusted.
- Reconnects reuse the live session of the same SteamID, and a session of a player who left is kept only until its last save completes. When a player reconnects while their session is still in memory, nothing is loaded again and the join announcement for top players is **not** repeated. This is intentional.
- If loading a player's data from the database fails (for example the database is down), the plugin retries from the game thread with a growing delay of 15, 30, 60 and then every 120 seconds, and the player's stats stay untouched in the meantime. Until the load succeeds, commands answer that the data is still loading.

## Diagnostics

`css_rank_status` prints everything needed to tell a configuration problem from a database problem:

```text
[Umbrella Ranked] Status
Backend: Sqlite | Config: on | CVar: on | Competitive: on | Mode: Points
Map: de_dust2 | Map blocked: no
Players: connected 12, loaded 12, loading 0, sessions 12
Pending saves: 3 | Autosave: on | Menus: 1
Top cache: 4 entries | TTL: 20s | Last refresh: 2026-08-27 01:14:02 UTC
Last autosave OK: 2026-08-27 01:13:44 UTC | Last error: none
```

- **Competitive: off**: check `Enabled`, `css_rank_enabled` and `Map blocked`.
- **loaded** lower than **connected** for a long time: the backend is not answering or a player is not authorized yet; see `Last error` and the log.
- **Last error** set while **Last autosave OK** is stale: writes are failing and stats are only in memory.

The plugin logs to the normal CounterStrikeSharp logs in `addons/counterstrikesharp/logs/`.

## Troubleshooting and FAQ

**The plugin does not load and the log says `failed to initialize its ... backend`.**
There is no fallback on purpose. For MySQL check host, port, credentials, that the database exists and that the user can create tables. For SQLite check that the plugin folder is writable and that `sqlite/` shipped with the plugin.

**SQLite on Linux fails with `GLIBC_2.3x not found`.**
You are running an old plugin build or a system without the Steam Runtime 3 container. 1.2.0 ships a SQLite library that needs glibc 2.28 at most. Use the Steam Runtime 3 "sniper" environment (the default for CS2 dedicated servers) or a distribution with glibc 2.28 or newer.

**Kills do not count.**
In order of likelihood:
1. Warmup is running: nothing counts during warmup.
2. Fewer than `MinimumPlayersForStats` real players are connected. Bots do not count as players.
3. The map matches `DisabledRankMapPatterns`, or `Enabled` is `false`, or `css_rank_enabled` is `0`. `css_rank_status` shows `Competitive: off`.
4. The player's data has not loaded yet or the player has no authorized SteamID (see [How players are identified](#how-players-are-identified)); `css_rank_status` shows `loading` or a lower `loaded` count, and the log has a warning after 60 seconds.

**I have kills but I am not in `top` or `rank` shows "unranked".**
Players need at least `MinimumKillsRequired` kills (never less than `100`). The same limit applies to the Top announcement.

**`rank` says the data is still loading.**
The load runs in the background right after the player joins, and a failed load is retried automatically (see above). If it never finishes, the database is unreachable: read `Last error` in `css_rank_status`.

**Can two servers share the ranking?**
Yes, with MySQL. Give them the same database, update every server to the same plugin version, and keep `MinimumKillsRequired` equal.

**Can I reset a player or the whole ranking?**
Players can reset themselves with `!resetrank` (subject to `AllowResetRank` and `ResetRankCooldownDays`); playtime is preserved. There is no command to wipe everyone: clear the two `ur_cs2_` tables in the database.

**What happens to unsaved stats on unload or crash?**
The final flush on unload waits at most 30 seconds. If the database does not answer in that time, whatever was not saved is lost (the log says so). A map-end flush that does not finish is not lost: it is retried by the next autosave. A server crash loses at most the stats since the last save.

## Database schema

Two tables, both using the fixed `ur_cs2_` prefix:

| Table | Contents |
| --- | --- |
| `ur_cs2_player_stats` | One row per player (columns below) |
| `ur_cs2_weapon_stats` | One row per player and weapon: `kills`, `headshots`. Weapon names are at most 64 characters |

| Column | Meaning |
| --- | --- |
| `steamid` | SteamID64 as text, for example `76561197960265928` |
| `name` | Last known name |
| `kills`, `deaths`, `assists`, `points` | Ranking stats |
| `headshots`, `mvps` | Headshot kills and round MVP awards |
| `rounds_won`, `rounds_lost` | Rounds that ended with the player on the winning or the losing side |
| `rounds_ct`, `rounds_t` | Rounds the player finished as CT or as T |
| `matches_won`, `matches_lost`, `matches_tied` | Match results, taken from the final score (a surrender counts as a loss for the team that surrendered) |
| `playtime` | Seconds on the server, which powers `toptime` |
| `last_seen`, `last_reset` | Unix timestamps |

Rounds and matches are only counted while competitive ranking is active, never during warmup, and not at all in free-for-all modes (`mp_teammates_are_enemies 1`), where the T and CT sides mean nothing.

Resetting a rank clears every stat and the weapon stats but **preserves playtime**. Pruning deletes inactive players from both tables. Both clear the leaderboard cache.

## Building from source

```bash
dotnet build -c Release
```

Output lands in `bin/Release/net10.0/`. The SQLite native libraries for Windows and Linux are copied into `sqlite/` automatically.

<details>
<summary><b>Project layout</b></summary>

```text
UmbrellaRanked/
  Config/     configuration model and enums
  Core/       sessions, ranking, autosave, playtime, cooldowns
  Data/       repositories, SQL dialects, schema initialization
  Menus/      WASD menu system
  Models/     records and DTOs
  Utils/      Steam ID conversion and input sanitization
  lang/       translations
  samples/    example configs
```

| File | Responsibility |
| --- | --- |
| `UmbrellaRankedPlugin.cs` | Lifecycle, commands, game events, menus, announcements, config limits |
| `Core/RankService.cs` | Load, save and reset orchestration, leaderboard caching |
| `Core/PlayerSessionService.cs` | Live session tracking, identity resolution and reconnect safety |
| `Core/AutosaveService.cs` | Non-overlapping autosaves and flushes |
| `Menus/WasdMenuService.cs` | WASD menu rendering and input |
| `Data/*Repository.cs` | MySQL and SQLite repositories (Dapper, fully parameterized) |
| `Data/SchemaInitializer.cs` | Table, column and index creation, Steam2 migration |

</details>

<details>
<summary><b>Behaviour notes</b></summary>

- Competitive stats only count once `MinimumPlayersForStats` real players are connected; playtime always counts.
- Blocked map patterns pause competitive ranking only; playtime and `toptime` keep working.
- Player stats are held in memory during a session and written by the autosave, on disconnect, on map end and on unload.
- The map-end and unload flushes are bounded (10 and 30 seconds), so an unreachable database cannot stall the game thread. After a map-end timeout the unsaved stats stay in memory and the next autosave retries them; after an unload timeout they are lost.

</details>

## License

Released under the [MIT License](LICENSE).
