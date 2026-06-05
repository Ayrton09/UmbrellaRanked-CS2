# SourceMod to CounterStrikeSharp migration notes

## Preserved on purpose

- Steam IDs are still stored as Steam2 strings, not SteamID64.
- Table names now use the CS2-specific `ur_cs2_` prefix: `ur_cs2_player_stats` and `ur_cs2_weapon_stats`.
- Ranking can now be selected with `RankingMode`.
- `RankingMode = Kda` keeps the KDA-style ranking behavior.
- `RankingMode = Points` uses the new CS2 point model while keeping KDA as a tie breaker.
- Reset behavior is unchanged in intent:
  - kills reset to `0`
  - deaths reset to `0`
  - assists reset to `0`
  - points reset to `0`
  - weapon stats are deleted
  - playtime is preserved
  - `last_reset` is updated
- SourceMod chat aliases were preserved, including plain chat words.

## CounterStrikeSharp equivalents

- SourceMod console commands -> `css_*` commands registered through `AddCommand`.
- SourceMod `say`/`say_team` chat listeners -> `AddCommandListener("say")` and `AddCommandListener("say_team")`.
- SourceMod translations phrase file -> CounterStrikeSharp `lang/*.json`.
- SourceMod cvars / `AutoExecConfig` -> strongly typed JSON config.
- SourceMod timers -> CounterStrikeSharp timers plus async tasks.
- SourceMod DB handle callbacks -> async repository methods with Dapper and parameterized SQL.

## Database and persistence changes

- The port uses a repository layer instead of inline SQL in gameplay code.
- All writes are parameterized.
- Saves write full snapshots to avoid double-counting from overlapping save paths.
- Autosaves are protected by a lock so a second autosave does not overlap an active flush.
- Disconnect, map end, and unload all force a flush path.
- No automatic fallback between MySQL and SQLite exists in this port.

## Unavoidable CS2 differences

These are deliberate nearest-safe equivalents, not silent feature removals:

- Admin access is mapped from SourceMod root admin to CounterStrikeSharp permission `@css/root`.
- Ranked list menus are implemented with the plugin's own internal WASD menu service. They render through center HTML and handle CS2 player button input directly, without `ChatMenu`, `CenterHtmlMenu`, or third-party menu frameworks.
- The original plugin emitted the Top #1 sound with SourceMod sound APIs. This port defaults to client-side `play <sound>` because that is the closest simple CS2 equivalent. A world sound mode is also exposed for operators who prefer it.
- SourceMod cvar live-edit behavior is replaced with config reload / hot reload semantics typical for CounterStrikeSharp plugins.
- Point scoring uses CounterStrikeSharp-exposed CS2 events: kills, assists, round MVP, bomb plant, bomb defuse, bomb explode, hostage rescue, and round team win. If a game mode does not fire one of those events, that specific point source naturally will not be awarded.

## Database schema

This build creates and uses only these CS2-specific tables:

- `ur_cs2_player_stats(steamid, name, kills, deaths, assists, points, playtime, last_seen, last_reset)`
- `ur_cs2_weapon_stats(steamid, weapon, kills)`

On first startup, the plugin creates the prefixed tables, columns, and indexes it needs. It does not rename, copy, or migrate data from any older table name.

## Operational guidance

- Choose one backend explicitly and keep it that way.
- If you move from SQLite to MySQL or back later, move data intentionally outside the plugin.
- Do not rely on backend auto-recovery; startup should fail loudly when the chosen backend is broken.
