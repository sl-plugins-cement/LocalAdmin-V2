# LocalAdmin V2 [![GitHub release](https://flat.badgen.net/github/release/northwood-studios/LocalAdmin-V2)](https://GitHub.com/northwood-studios/LocalAdmin-V2/releases/) [![Project License](https://flat.badgen.net/github/license/northwood-studios/LocalAdmin-V2)](https://github.com/northwood-studios/LocalAdmin-V2/blob/master/LICENSE)
Second version of LocalAdmin, tool for hosting dedicated servers for SCP: Secret Laboratory.

This version is compatible with game version 9.0.0 and newer. Compatible both with Windows and Linux x64.

# Syntax
Windows: `LocalAdmin.exe [port] [arguments] [-- arguments passthrough]`

Linux: `./LocalAdmin [port] [arguments] [-- arguments passthrough]`

# Enhanced fork features

This fork keeps the upstream LocalAdmin command flow and adds operational features for detached/headless SCP:SL servers.

## Server application path

`server_application_path` can point to either the SCP:SL server executable or the directory that contains it. Relative paths are resolved against the LocalAdmin config directory first, then the current working directory.

If `server_application_path` is empty, LocalAdmin tries:

- the current working directory
- the LocalAdmin executable directory
- Steam library locations for the SCP:SL Dedicated Server app

When a server executable is found, LocalAdmin uses that executable's directory as the game working directory.

## Headless command channel

Enable the named-pipe command channel in the LocalAdmin config:

```text
command_channel_enabled: true
command_channel_pipe_name: LocalAdmin-Console
```

The server creates a per-port pipe. On Windows, the actual pipe name includes the current user's SID to avoid cross-account collisions, and a DACL limits access to the current user plus Administrators; `client/la-send.ps1` reconstructs the name automatically. On Linux, the pipe is created with `PipeOptions.CurrentUserOnly` so only the current user can connect (the client sets the same flag).

Examples:

```powershell
pwsh -File .\client\la-send.ps1 -Port 7777 -Command "players"
pwsh -File .\client\la-send.ps1 -Port 7777 -Command "help"
```

The command channel enqueues the same command text that a console operator would type, then streams recent LocalAdmin console output back to the caller until the reply settles.

## Restart when empty

Enable the built-in restart-when-empty watchdog in the LocalAdmin config:

```text
auto_restart_empty_enabled: true
restart_at_or_below_players: 1
empty_confirmations: 2
empty_duration_seconds: 0
policy_tick_seconds: 30
recent_activity_over_players: 5
recent_activity_window_minutes: 30
```

Each tick the watchdog asks the server for an **authoritative** player count (it sends the `players` command and parses the `List of players (N):` reply; the game's idle signal is also treated as an authoritative `0`). It only ever acts on a *fresh* reading, so a stale or still-booting count can never trigger a restart. It restarts only when:

- the authoritative player count is at or below `restart_at_or_below_players`
- the low-pop condition holds for `empty_confirmations` policy ticks
- the low-pop condition has held for at least `empty_duration_seconds` (set `0` to gate on confirmations only)
- the rolling `recent_activity_window_minutes` history is fully observed
- that recent window never peaked above `recent_activity_over_players`

Set `recent_activity_window_minutes` to `0` to disable the recent-activity guard. Restarts are scheduled through the normal graceful game-side `exit` path and fall back to a LocalAdmin-controlled restart if graceful exit stalls. A built-in cooldown prevents an always-empty server from restart-looping tightly.

## One-shot restart when empty command

The LocalAdmin command `restartwhenempty` arms, reports, or cancels a one-shot restart-when-empty watchdog. Unlike the permanent `auto_restart_empty_enabled` toggle, this fires exactly once - use it to schedule a single restart the next time the server empties out (e.g. to apply a pending update once the players leave).

```text
restartwhenempty once
restartwhenempty once [threshold] [confirmations] [tickSeconds] [recentOverPlayers] [windowMinutes] [durationSeconds]
restartwhenempty status
restartwhenempty cancel
```

- `threshold` - minimum player count; restart at or below this many players.
- `durationSeconds` - require the low count to hold this long before restarting (in addition to `confirmations`).

Named options are accepted:

```text
restartwhenempty once threshold=0 confirmations=1 tick=15 recentOver=5 window=0 duration=120
```

Aliases:

```text
threshold: threshold, at, players, restartAt, restartAtOrBelow
confirmations: confirmations, confirms, confirm
tick: tick, tickSeconds, seconds, poll, pollSeconds
recentOver: recentOver, recentActivityOver, over
window: window, recentWindow, recentActivityWindow, minutes
duration: duration, durationSeconds, for, sustained
```

`restartwhenempty once` uses the configured permanent-watchdog defaults (including `empty_duration_seconds`) when arguments are omitted. The one-shot **persists across unrelated restarts** (a crash/heartbeat/round restart re-arms it in the next session) until it fires or is canceled, and it refuses to replace an already armed one-shot until `restartwhenempty cancel` is run.

The command also works through the command-channel client:

```powershell
pwsh -File .\client\la-send.ps1 -Port 7777 -Command "restartwhenempty status"
pwsh -File .\client\la-send.ps1 -Port 7777 -Command "restartwhenempty once 0 1 tick=15 window=0"
pwsh -File .\client\la-send.ps1 -Port 7777 -Command "restartwhenempty cancel"
```

## Restart on next round restart

The LocalAdmin command `restartnextround` arms, reports, or cancels a one-shot that gracefully restarts the server the next time it performs a round restart (the game's initial round at server boot is not counted). Like the one-shot above, it persists across unrelated restarts until it fires or is canceled.

```text
restartnextround
restartnextround once
restartnextround after=<rounds>
restartnextround status
restartnextround cancel
```

`after=<rounds>` (default `1`; aliases `rounds`, `count`, `n`) fires after that many round restarts. Examples through the command-channel client:

```powershell
pwsh -File .\client\la-send.ps1 -Port 7777 -Command "restartnextround"
pwsh -File .\client\la-send.ps1 -Port 7777 -Command "restartnextround after=3"
pwsh -File .\client\la-send.ps1 -Port 7777 -Command "restartnextround status"
pwsh -File .\client\la-send.ps1 -Port 7777 -Command "restartnextround cancel"
```

## Build and publish

Windows x64:

```powershell
dotnet build ".\LocalAdmin V2.csproj" -c Release -r win-x64
dotnet publish ".\LocalAdmin V2.csproj" -c Release -r win-x64
```

Published output is written under:

```text
bin\Release\net8.0-windows\win-x64\publish\
```

# Arguments list
| Long version | Short version | Description |
| --- | --- | --- |
| --reconfigure | -r | Opens configuration editor. |
| --config [path to file] | | Changes LocalAdmin config path. |
| --logs [path to logs folder] | | Changes LocalAdmin logs directory. |
| --gameLogs [path to logs folder] | | Changes game logs directory.<br>**It applies only to the Log Cleaner in LocalAdmin, not the game itself!** |
| --useDefault | -d | Uses the default config if no config is present. |
| --printStd | -s | Redirects stdout and stderr of the game to the LocalAdmin live view. |
| --noSetCursor | -c | Disables setting console cursor position. |
| --printControl | -p | Enables printing control messages. |
| --noLogs | -l | Disables LocalAdmin logging. |
| --noAutoFlush | -n | Disables auto flush of LocalAdmin log files.<br>**Not compatible with --noLogs argument.** |
| --noAlign | -a | Disables multiline log entries alignment. |
| --noTerminalTitle | -t | Disables LocalAdmin status in the terminal title. |
| --dismissPluginManagerSecurityWarning | | Dismisses Plugin Manager security warning. |
| --disableTrueColor | | Disables True Color output. |
| --skipHomeCheck | | Skips `home` env var checking on startup (linux only). |
| --restartsLimit |  | Specifies a limit of auto-restarts in a specified time window.<br>Setting this argument to 0 disables auto-restarts.<br>Setting this argument to -1 disables the limit.<br>*Default value: 4* |
| --restartsTimeWindow |  | Specifies a time window (in seconds) for the auto-restarts limit.<br>Setting this argument to 0 disables resetting the amount of auto-restarts after a specified amount of time.<br>*Default value: 480* |
| --logLengthLimit |  | Specifies the limit of characters in LocalAdmin log file.<br>Suffixes `k`, `M`, `G` and `T` are supported, eg. `5G` is equal to `5000000000` characters.<br>Setting this argument to 0 disables the limit.<br>*Default value: 25G* |
| --logEntriesLimit |  | Specifies the limit of entries in LocalAdmin log file.<br>Suffixes `k`, `M`, `G` and `T` are supported, eg. `5G` is equal to `5000000000` entries.<br>Setting this argument to 0 disables the limit.<br>*Default value: 10G* |
| --acceptEULA | | Accepts the [End User License Agreement (EULA)](https://link.scpslgame.com/eula) without prompting. Alternatively, you can set `ACCEPT_SCPSL_EULA=TRUE` in environment variables to automatically accept the [EULA](https://link.scpslgame.com/eula). |
