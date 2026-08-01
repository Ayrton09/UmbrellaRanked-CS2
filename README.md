# Umbrella Ranked

A native [CounterStrikeSharp](https://docs.cssharp.dev/) ranking system for CS2, with MySQL or SQLite storage, an in-game WASD menu, and full localization.

## Features

- Tracks kills, deaths, assists, KDA, points, playtime, and per-weapon kills.
- Two ranking modes: `Points` or `Kda`.
- MySQL or SQLite, selected explicitly through `DatabaseMode` — no silent fallback.
- Built-in WASD menu for the leaderboards, with pagination, per-entry detail pages, and a reset confirmation prompt.
- Join announcements for top-ranked players, with an optional Top #1 sound.
- Autosave plus saves on disconnect, map end, and plugin unload.
- Per-player command cooldown to prevent chat spam.
- Localization through CounterStrikeSharp `lang/*.json` (English, Spanish, Portuguese, Russian, Chinese).
- Runtime toggle via `css_rank_enabled`, and map patterns that pause competitive ranking while playtime keeps counting.

## Requirements

- CounterStrikeSharp `1.0.371` or newer (plugin API `175`+)
- .NET 10 runtime
- MySQL 5.7+ / MariaDB, or nothing extra if you use SQLite

## Installation

1. Download the release archive and extract it into your server's `game/csgo/` directory. It contains the full `addons/` tree.
2. Edit the config at `addons/counterstrikesharp/configs/plugins/umbrellaranked/umbrellaranked.json`.
3. Restart the server or reload the plugin.

Files that must ship next to the plugin DLL:

```text
addons/counterstrikesharp/plugins/umbrellaranked/
  umbrellaranked.dll
  umbrellaranked.deps.json
  Dapper.dll
  MySqlConnector.dll
  Microsoft.Extensions.DependencyInjection.Abstractions.dll
  Microsoft.Extensions.Logging.Abstractions.dll
  lang/                 # required
  sqlite/               # required only when DatabaseMode = Sqlite
```

Tables and indexes are created automatically on first load.

## Commands

### Players

Each command works as `!cmd`, `/cmd`, and as a plain chat word.

| Command | Description |
| --- | --- |
| `rank` | Show your position and stats |
| `top` | Open the points/KDA leaderboard |
| `toptime` | Open the playtime leaderboard |
| `topweapons` / `toparmas` | Open the per-weapon leaderboard menu |
| `resetrank` / `rrank` | Reset your own stats (asks for confirmation) |

### Admins

Both require the `@css/root` permission.

| Command | Description |
| --- | --- |
| `css_rank_status` | Print backend, session, cache and autosave diagnostics |
| `css_rank_prunenow` | Run the inactive-player prune immediately |

### Console variable

```cfg
css_rank_enabled 1
```

Setting it to `0` immediately pauses competitive rank tracking and closes the rank and weapon menus. Playtime tracking and `toptime` keep working.

## Menu controls

| Key | Action |
| --- | --- |
| `W` / `S` | Move selection |
| `A` / `D` | Previous / next page |
| `E` | Select |
| `R` | Back |
| `Jump` / `Duck` | Close |

Menus close automatically after 45 seconds of inactivity, and on death or team change.

## Configuration

Start from [samples/UmbrellaRanked.mysql.sample.json](samples/UmbrellaRanked.mysql.sample.json).

| Setting | Default | Description |
| --- | --- | --- |
| `Enabled` | `true` | Master switch for competitive ranking |
| `DatabaseMode` | `MySql` | `MySql` or `Sqlite` |
| `RankingMode` | `Points` | `Points` or `Kda` |
| `MinimumKillsRequired` | `100` | Kills needed to appear in ranked lists (minimum `100`) |
| `MinimumPlayersForStats` | `4` | Real players needed before competitive stats count |
| `DisabledRankMapPatterns` | surf/mg/bhop/jb/dr | Map patterns where competitive ranking pauses (`*` wildcard) |
| `CommandCooldownSeconds` | `3.0` | Per-player anti-spam delay |
| `AutosaveIntervalSeconds` | `120.0` | Periodic save interval; `0` disables |
| `PruneInactiveDays` | `35` | Delete players unseen for this many days. **`0` disables pruning**; any other value has a floor of `35` |
| `PruneOnStartup` | `false` | Run a prune when the plugin loads |
| `PruneCheckIntervalHours` | `6.0` | Scheduled prune interval; `0` disables |
| `AllowResetRank` | `true` | Allow players to reset their own stats |
| `ResetRankCooldownDays` | `30` | Cooldown between self-resets; `0` disables |
| `TopAnnouncementThreshold` | `5` | Announce joining players ranked within this position; `0` disables |
| `LeaderboardLimit` | `50` | Rows fetched for the leaderboards |
| `TopCacheSeconds` | `20.0` | Leaderboard cache lifetime; `0` disables caching |
| `Top1Sound` | see below | Sound played when a Top #1 player joins |

Out-of-range values are clamped on load and the adjustment is written to the server log.

### Database

MySQL requires `Host`, `Database` and `Username` to be set, otherwise the plugin refuses to load.

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

SQLite only needs a path, resolved relative to the plugin directory when not absolute.

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

`PlaybackMode` accepts `Disabled`, `ClientCommand` (uses `playvol`, needs no precache) or `SoundEvent` (uses a precached sound event; set `ResourcePath` when the resource differs from `Value`).

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

Resulting behaviour:

| Event | Attacker | Victim |
| --- | --- | --- |
| Normal kill | `+2` | `-2` |
| Headshot kill | `+3` | `-2` |
| Knife kill | `+5` | `-5` |
| Zeus / taser kill | `+4` | `-4` |
| Suicide | — | `-5` |
| Teamkill | `-5` | no change |

Bonuses stack, so a knife headshot is `+6` for the attacker. Negative point values in the config are clamped to `0`, and a player's total can never drop below `0`.

## Database schema

Two tables, both using the fixed `ur_cs2_` prefix:

- `ur_cs2_player_stats` — one row per player: name, kills, deaths, assists, points, playtime, `last_seen`, `last_reset`
- `ur_cs2_weapon_stats` — one row per player and weapon

Steam IDs are stored in Steam2 format (`STEAM_1:0:12345`). Playtime lives in `ur_cs2_player_stats.playtime` and powers `toptime`.

Resetting a rank clears kills, deaths, assists, points and weapon stats, but **preserves playtime**. Pruning deletes inactive players from both tables and never touches players who are currently connected.

## Building from source

```bash
dotnet build -c Release
```

Output lands in `bin/Release/net10.0/`. The SQLite native libraries for Windows and Linux are copied into `sqlite/` automatically.

## Project layout

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

Main components:

- `UmbrellaRankedPlugin.cs` — lifecycle, commands, game events, menus, announcements
- `Core/RankService.cs` — load, save and reset orchestration, leaderboard caching
- `Core/PlayerSessionService.cs` — live session tracking and reconnect safety
- `Core/AutosaveService.cs` — non-overlapping autosaves and flushes
- `Menus/WasdMenuService.cs` — WASD menu rendering and input
- `Data/*Repository.cs` — MySQL and SQLite repositories (Dapper, fully parameterized)
- `Data/SchemaInitializer.cs` — table, column and index creation

## Behaviour notes

- Competitive stats only count once `MinimumPlayersForStats` real players are connected; playtime always counts.
- Blocked map patterns pause competitive ranking only — playtime and `toptime` keep working.
- Player stats are held in memory during a session and written by the autosave, on disconnect, on map end, and on unload.

## License

Released under the [MIT License](LICENSE).
