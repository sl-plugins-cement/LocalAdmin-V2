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

The LocalAdmin executable includes a native cross-platform client, so a Linux host does not need PowerShell or a second runtime:

```bash
# Raw/local console text. Recognized LocalAdmin commands stay in LocalAdmin; other commands
# are forwarded to the SCP:SL server console.
./LocalAdmin ctl 7777 console players
./LocalAdmin ctl 7777 localadmin "restartwhen status"

# Native SCP:SL Remote Admin path. `ra` adds the required leading slash.
./LocalAdmin ctl 7777 ra pm reload
```

`raw`, `console`, `local`, and `localadmin` are equivalent exact-text modes. The mode may be omitted (`./LocalAdmin ctl 7777 players`). `ra` is the convenience mode and does not add a second slash when the command already starts with one. Run `./LocalAdmin ctl --help` for pipe-name and timeout options.

Client exit status describes transport/delivery, not the command's semantic result:

- `0` - LocalAdmin echoed the submitted line, confirming delivery. An RA command can still return a failure response; inspect the printed response.
- `2` - the client could not connect to the pipe.
- `3` - the pipe connected, but the command echo was not observed before timeout, so delivery is unconfirmed.
- `64` - invalid client syntax.

The read-back protocol captures console output after the matching command echo until the configured quiet window. It does not expose SCP:SL's internal RA boolean result as a process exit code.

The pipe is intentionally same-user only. When LocalAdmin runs as a service account, the client must run as that account too. `client/scpsl-ctl` is a POSIX wrapper template that uses non-interactive `sudo` and provides concise SSH automation:

```bash
scpsl-ctl console players
scpsl-ctl ra pm reload
scpsl-ctl localadmin restartwhen status
```

Install the wrapper somewhere such as `/usr/local/bin/scpsl-ctl`, install the root-owned `client/scpsl-ctl.conf.example` as `/etc/default/scpsl-ctl`, and adjust the binary path, service user, port, pipe base, or lock settings there. The caller needs permission to run `flock` plus the configured LocalAdmin binary as the service user. The wrapper runs both `flock` and the client as that account, serializing canonical SSH invocations so their broadcast-based reply windows cannot overlap. Lock timeout exits with status `75`. Direct `LocalAdmin ctl` and the legacy PowerShell client do not take this wrapper lock, so do not run them concurrently with other command-channel clients. The old `client/la-send.ps1` remains supported for Windows and existing automation.

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

## One-shot restart intents (`restartwhen`)

The LocalAdmin command `restartwhen` arms a predefined one-shot restart condition by preset name, so an operator or runtime agent selects a vetted preset instead of authoring raw parameters. Exactly one intent can be armed at a time; it fires exactly once.

```text
restartwhen <preset>
restartwhen list
restartwhen status
restartwhen cancel
```

Presets (see `restartwhen list` for the live catalog):

| Preset             | Fires |
| ------------------ | ----- |
| `next-round`       | On the next round restart, regardless of player count. |
| `next-round-lowpop`| On the next round restart that ends with fewer than 5 players; otherwise waits for a later round. |
| `empty`            | Once the server has been empty for 30 sustained minutes. |
| `low-30m`          | Once fewer than 2 players have been on for 30 sustained minutes. |
| `quiet-hour`       | As `low-30m`, and additionally the last hour never peaked above 4 players. |

Conditions combine one **edge** (the event that evaluates the intent: a round restart, or a low-activity watchdog tick) with optional **gates** (state checks at that moment, e.g. a fresh player count at or below a threshold). A player-gated round preset only acts on a fresh authoritative player count; if none is available at the round edge, it skips that round rather than restart on stale data.

### Custom presets (JSON files)

Beyond the built-ins, a preset can be authored on demand as a JSON file in `restart_presets/` under the LocalAdmin config directory (next to `localadmin_internal_data.json`). The preset name is the filename without extension (letters, digits, and dashes only); files are read fresh on each `restartwhen` invocation, so no reload or restart is needed. Built-in names always win, so a file can never shadow a vetted preset.

```jsonc
// restart_presets/late-night.json  ->  restartwhen late-night
{
  "summary": "Restart at the next round end with fewer than 3 players.",
  "edge": "round-end",        // or "low-activity"
  "maxPlayers": 2             // gate: fresh player count must be <= this
}
```

Fields (all optional except `edge`; unknown fields are rejected so typos fail loudly):

- `edge` - `"round-end"` (evaluate at a round restart) or `"low-activity"` (evaluate on a watchdog tick; requires `maxPlayers`).
- `rounds` - for `round-end`: fire on the Nth qualifying round restart (default `1`).
- `maxPlayers` - player gate; omit for an ungated round restart.
- `sustainedSeconds` - for `low-activity`: the low state must hold this long (default `0`).
- `confirmations`, `tickSeconds` - for `low-activity`: consecutive confirming ticks (default `2`) and poll interval (default `30`).
- `activityWindowMinutes`, `activityOverPlayers` - recent-activity guard: block firing if the last N minutes peaked above the threshold (default: disabled).
- `summary` - shown in `restartwhen list`; defaults to a generated description.

Invalid files are listed with their parse error in `restartwhen list` instead of being silently skipped.

The armed intent **persists across child-game session restarts while the same LocalAdmin process remains alive** (a crash, heartbeat restart, or round restart re-arms it in the next session) until it fires or is canceled, and arming refuses to replace an already armed intent until `restartwhen cancel` is run. The intent is held in memory: a LocalAdmin/systemd restart or host reboot loses it, so check `restartwhen status` and re-arm afterward. An intent is consumed only when a graceful restart is actually committed; if a crash or another shutdown wins the race, the intent stays armed and retries.

Examples through the native command-channel client:

```bash
./LocalAdmin ctl 7777 localadmin restartwhen list
./LocalAdmin ctl 7777 localadmin restartwhen next-round-lowpop
./LocalAdmin ctl 7777 localadmin restartwhen status
./LocalAdmin ctl 7777 localadmin restartwhen cancel
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

Linux x64 (run on Linux or WSL; NativeAOT publishing is OS-specific):

```bash
dotnet build "./LocalAdmin V2.csproj" -c Release -r linux-x64
dotnet publish "./LocalAdmin V2.csproj" -c Release -r linux-x64
```

Published output is written under:

```text
bin/Release/net10.0/linux-x64/publish/
```

Install the published launcher outside the SCP:SL game directory (for example,
`/opt/scpsl-localadmin/releases/<release>/LocalAdmin`). Do not copy its runtime or
launcher files into the game root; Unity can load conflicting assemblies from there.

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
