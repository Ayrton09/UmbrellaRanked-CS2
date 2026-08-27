<h1 align="center">Umbrella Ranked</h1>

<p align="center">
  A native <a href="https://docs.cssharp.dev/">CounterStrikeSharp</a> ranking system for CS2 —
  points, KDA, playtime and per-weapon stats, with an in-game WASD menu and full localization.
</p>

<p align="center">
  <a href="https://github.com/Ayrton09/UmbrellaRanked-CS2/actions/workflows/build.yml"><img alt="Build" src="https://github.com/Ayrton09/UmbrellaRanked-CS2/actions/workflows/build.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-blue"></a>
  <img alt="Version" src="https://img.shields.io/badge/version-1.0.3-informational">
  <img alt="CounterStrikeSharp" src="https://img.shields.io/badge/CounterStrikeSharp-1.0.373%2B-orange">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-512BD4">
</p>

---

## Contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Commands](#commands)
- [Menu controls](#menu-controls)
- [Configuration](#configuration)
- [Points](#points)
- [Diagnostics](#diagnostics)
- [Database schema](#database-schema)
- [Building from source](#building-from-source)
- [License](#license)

## Features

|  | |
| --- | --- |
| **Stats** | Kills, deaths, assists, KDA, points, playtime and per-weapon kills |
| **Ranking** | Two modes — `Points` or `Kda` — with a full tie-breaker chain so positions are stable |
| **Storage** | MySQL or SQLite, chosen explicitly through `DatabaseMode`. No silent fallback: a misconfigured backend refuses to load instead of quietly losing data |
| **Menus** | Built-in WASD menu with pagination, per-entry detail pages and a reset confirmation prompt |
| **Announcements** | Join announcements for top-ranked players, with an optional Top #1 sound |
| **Durability** | Periodic autosave plus saves on disconnect, map end and plugin unload |
| **Localization** | English, Spanish, Portuguese, Russian and Chinese via CounterStrikeSharp `lang/*.json` |
| **Control** | Runtime toggle through `css_rank_enabled`, and map patterns that pause competitive ranking while playtime keeps counting |

## Requirements

- **CounterStrikeSharp `1.0.373`** or newer — the plugin API version is the CounterStrikeSharp build number, so `1.0.373` reports `373`
- **.NET 10** runtime
- **MySQL 5.7+ / MariaDB** — or nothing extra if you use SQLite

## Installation

1. Download the release archive and extract it into your server's `game/csgo/` directory. It contains the full `addons/` tree.
2. Edit `addons/counterstrikesharp/configs/plugins/umbrellaranked/umbrellaranked.json`.
3. Restart the server, or reload the plugin.

Tables and indexes are created automatically on first load.

### Fastest path — SQLite

Set `DatabaseMode` to `Sqlite` and you are done; the database file is created next to the plugin.

```json
"DatabaseMode": "Sqlite"
```

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

> [!IMPORTANT]
> When upgrading, replace `lang/` too. Missing keys fall back to the raw key name in chat.

## Commands

### Players

Each command works as `!cmd`, `/cmd`, and as a plain chat word.

| Command | Description |
| --- | --- |
| `rank` | Show your position and stats |
| `top` | Open the points/KDA leaderboard |
| `toptime` | Open the playtime leaderboard |
| `topweapons` · `toparmas` | Open the per-weapon leaderboard menu |
| `resetrank` · `rrank` | Reset your own stats, with a confirmation prompt |

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

Start from [`samples/UmbrellaRanked.mysql.sample.json`](samples/UmbrellaRanked.mysql.sample.json). Out-of-range values are clamped on load and the adjustment is written to the server log.

### General

| Setting | Default | Description |
| --- | --- | --- |
| `Enabled` | `true` | Master switch for competitive ranking |
| `DatabaseMode` | `MySql` | `MySql` or `Sqlite` |
| `RankingMode` | `Points` | `Points` or `Kda` |
| `MinimumKillsRequired` | `100` | Kills needed to appear in ranked lists. Floor of `100` |
| `MinimumPlayersForStats` | `4` | Real players needed before competitive stats count |
| `DisabledRankMapPatterns` | surf / mg / bhop / jb / dr | Map patterns where competitive ranking pauses. Supports the `*` wildcard |
| `CommandCooldownSeconds` | `3.0` | Per-player anti-spam delay |
| `AllowResetRank` | `true` | Allow players to reset their own stats |
| `ResetRankCooldownDays` | `30` | Cooldown between self-resets. `0` disables |
| `TopAnnouncementThreshold` | `5` | Announce joining players ranked within this position. `0` disables |

### Persistence and maintenance

| Setting | Default | Description |
| --- | --- | --- |
| `AutosaveIntervalSeconds` | `120.0` | Periodic save interval. `0` disables |
| `PruneInactiveDays` | `35` | Delete players unseen for this many days. **`0` disables pruning**; any other value has a floor of `35` |
| `PruneOnStartup` | `false` | Run a prune when the plugin loads |
| `PruneCheckIntervalHours` | `6.0` | Scheduled prune interval. `0` disables |
| `LeaderboardLimit` | `50` | Rows fetched for the leaderboards |
| `TopCacheSeconds` | `20.0` | Leaderboard cache lifetime. `0` disables caching |

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

SQLite only needs a path, resolved relative to the plugin directory when it is not absolute.

```json
"Sqlite": {
  "FilePath": "data/umbrella_ranked.sqlite",
  "BusyTimeoutSeconds": 5,
  "UseWriteAheadLogging": true
}
```

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
| `ClientCommand` | Uses `playvol`. Needs no precache |
| `SoundEvent` | Uses a precached sound event. Set `ResourcePath` when the resource differs from `Value` |

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
  "TeamWin": 1,
  "TeamLossPenalty": 1
}
```

With the defaults above:

| Event | Attacker | Victim |
| --- | --- | --- |
| Normal kill | `+2` | `-2` |
| Headshot kill | `+3` | `-2` |
| Knife kill | `+5` | `-5` |
| Zeus / taser kill | `+4` | `-4` |
| Suicide | — | `-5` |
| Teamkill | `-5` | no change |

Bonuses stack, so a knife headshot is `+6` for the attacker. Negative point values in the config are clamped to `0`, and a player's total can never drop below `0`.

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

- **Competitive: off** — check `Enabled`, `css_rank_enabled` and `Map blocked`.
- **loading** stuck above `0` — the backend is not answering; see `Last error`.
- **Last error** set while **Last autosave OK** is stale — writes are failing and stats are only in memory.

## Database schema

Two tables, both using the fixed `ur_cs2_` prefix:

| Table | Contents |
| --- | --- |
| `ur_cs2_player_stats` | One row per player: name, kills, deaths, assists, points, playtime, `last_seen`, `last_reset` |
| `ur_cs2_weapon_stats` | One row per player and weapon |

Steam IDs are stored in Steam2 format (`STEAM_1:0:12345`). Playtime lives in `ur_cs2_player_stats.playtime` and powers `toptime`.

Resetting a rank clears kills, deaths, assists, points and weapon stats but **preserves playtime**. Pruning deletes inactive players from both tables and never touches players who are currently connected.

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
| `UmbrellaRankedPlugin.cs` | Lifecycle, commands, game events, menus, announcements |
| `Core/RankService.cs` | Load, save and reset orchestration, leaderboard caching |
| `Core/PlayerSessionService.cs` | Live session tracking and reconnect safety |
| `Core/AutosaveService.cs` | Non-overlapping autosaves and flushes |
| `Menus/WasdMenuService.cs` | WASD menu rendering and input |
| `Data/*Repository.cs` | MySQL and SQLite repositories (Dapper, fully parameterized) |
| `Data/SchemaInitializer.cs` | Table, column and index creation |

</details>

<details>
<summary><b>Behaviour notes</b></summary>

- Competitive stats only count once `MinimumPlayersForStats` real players are connected; playtime always counts.
- Blocked map patterns pause competitive ranking only — playtime and `toptime` keep working.
- Player stats are held in memory during a session and written by the autosave, on disconnect, on map end and on unload.
- The map-end and unload flushes are bounded, so an unreachable database cannot stall the game thread; anything left unsaved stays in memory and is retried by the next autosave.

</details>

## License

Released under the [MIT License](LICENSE).
