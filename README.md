# LocalAdmin V2 [![GitHub release](https://flat.badgen.net/github/release/northwood-studios/LocalAdmin-V2)](https://GitHub.com/northwood-studios/LocalAdmin-V2/releases/) [![Project License](https://flat.badgen.net/github/license/northwood-studios/LocalAdmin-V2)](https://github.com/northwood-studios/LocalAdmin-V2/blob/master/LICENSE)
Second version of LocalAdmin, tool for hosting dedicated servers for SCP: Secret Laboratory.

This version is compatible with game version 9.0.0 and newer. Compatible both with Windows and Linux x64.

# Syntax
Windows: `LocalAdmin.exe [port] [arguments] [-- arguments passthrough]`

Linux: `./LocalAdmin [port] [arguments] [-- arguments passthrough]`

# Enhanced fork features

Managed activation runs the matching `scpsl-ops` `preflight` hook as the LocalAdmin
service account before arming a restart. Configuration, storage access, and startup-log
failures leave the running game alone and the prepared reservation cancellable. Upgrade
the CLI before this fork; an older CLI without the hook rejects activation safely.

For disposable DEV lanes, keep `ops_lane` for typed command identity and set
`ops_managed_activation_enabled: false`. LocalAdmin then starts the installed game without
checkpoint bootstrap, activation hooks, or managed rollback. The default is `true`, preserving
managed production/mirror behavior. `scpsl-ops dev stage` directly replaces installed files;
`dev activate` only requests a restart. A crash or another restart can load the new files too.

DEV 可保留 `ops_lane` 并设置 `ops_managed_activation_enabled: false`，直接启动已安装文件，
不要求检查点、激活 hook 或托管回滚。正式服与镜像保留默认值 `true`。
启动部署前会使用 LocalAdmin 服务账户执行预检；预检失败不会停止游戏。请先更新配套
`scpsl-ops`，再更新 LocalAdmin。

This fork keeps the upstream LocalAdmin command flow and adds operational features for detached/headless SCP:SL servers.

## Server application path

`server_application_path` can point to either the SCP:SL server executable or the directory that contains it. Relative paths are resolved against the LocalAdmin config directory first, then the current working directory.

If `server_application_path` is empty, LocalAdmin tries:

- the current working directory
- the LocalAdmin executable directory
- Steam library locations for the SCP:SL Dedicated Server app

When a server executable is found, LocalAdmin uses that executable's directory as the game working directory.

To run a game build that frames its console output with big-endian lengths, such as Carl Mod, point
`server_application_path` at its executable (for example `Carl Mod.exe`) and set:

```text
sl_to_la_big_endian_length: true
```

With the wrong setting LocalAdmin reports an out-of-range message length and stops reading game output.

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

Set `recent_activity_window_minutes` to `0` to disable the recent-activity guard. Restarts are scheduled through SCP:SL's native `rnr` followed by native `restart` command sequence and fall back to a LocalAdmin-controlled restart if the native restart stalls. The watchdog never sends `exit`. A built-in cooldown prevents an always-empty server from restart-looping tightly.

## One-shot restart intents (`restartwhen`)

The LocalAdmin command `restartwhen` requests a predefined restart action by preset name, so an operator or runtime agent selects a vetted preset instead of authoring raw parameters. A preset either forwards a native SCP:SL command or arms a LocalAdmin-managed one-shot condition. Exactly one LocalAdmin-managed intent can be armed at a time; it fires exactly once.

```text
restartwhen <preset>
restartwhen list
restartwhen status
restartwhen cancel
```

Presets (`restartwhen list` shows the live catalog):

| Name | Behavior |
|---|---|
| `safe` | Restart as soon as a fresh count confirms zero players; otherwise at the next round end. |
| `low-pop` | Restart as soon as confirmed empty; otherwise at a round end with fewer than 5 players. |
| `now` | Restart immediately, even with players connected. |

Safe presets never poll. The empty-server fallback fires only on the game's own idle-mode signal (the `IdleEnter` control byte, or an idle state already known when arming); LocalAdmin does not send `players` to find out, and never infers an empty server from missing or stale data. Each request fires once and is cancellable until committed. `next-round` and `next-round-lowpop` remain compatibility aliases for existing callers; the old 30-minute presets are removed from the catalog. Managed deployment `-rnr` uses the safe behavior. Native game `rnr` remains game-owned and still waits for a round; use `restartwhen safe` for an empty-server fallback.

### Native `rnr` is a toggle, so LocalAdmin tracks it

Every LocalAdmin-driven restart is SCP:SL's own sequence: native `rnr` (restart after the round)
followed by native `restart` (round restart), which makes the game exit and LocalAdmin relaunch it.
`rnr` toggles: sending it while an operator already armed it would silently cancel their restart
and leave `restart` as a plain round restart. LocalAdmin therefore keeps a per-session belief about
the flag, fed by the game itself (the `ExitActionRestart`/`ExitActionReset` control bytes and the
`Server WILL/WON'T restart after next round.` reply), and sends `rnr` only when the flag is not
already armed. A round that restarts without the game exiting resets the belief. LocalAdmin disarms
the flag only when its own `rnr` armed it; an operator-armed `rnr` is left alone by cancellation.
Source: `CommandSystem/Commands/Shared/RestartNextRoundCommand.cs` and
`RoundRestarting/RoundRestart.cs` in the decompiled dedicated server.

中文：`safe` 在确认空服后立即重启，否则等待回合结束；`low-pop` 在空服时立即重启，否则等待人数少于 5 的回合结束；`now` 无论人数立即重启。只执行一次，不开启循环重启；未知或过期人数不会被当作空服。游戏原生 `rnr` 是开关：LocalAdmin 根据游戏的控制字节记录其状态，只在未开启时发送 `rnr`，不会误关他人已设置的重启；只取消自己开启的 `rnr`。空服回退请用 `restartwhen safe`。

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

A LocalAdmin-managed armed intent **persists across child-game session restarts while the same LocalAdmin process remains alive** (a crash, heartbeat restart, or round restart re-arms it in the next session) until it fires or is canceled, and arming refuses to replace an already armed intent until `restartwhen cancel` is run. The intent is held in memory: a LocalAdmin/systemd restart or host reboot loses it, so check `restartwhen status` and re-arm afterward. An intent is consumed only when a graceful restart actually commits; if a crash or another shutdown wins the race, the intent stays armed and retries. `next-round` uses the same LocalAdmin-managed, cancellable intent, so its status can be read back exactly before the CLI reports success.

Examples through the native command-channel client:

```bash
./LocalAdmin ctl 7777 localadmin restartwhen list
./LocalAdmin ctl 7777 localadmin restartwhen next-round-lowpop
./LocalAdmin ctl 7777 localadmin restartwhen status
./LocalAdmin ctl 7777 localadmin restartwhen cancel
```

## Managed activation supervision

This fork can supervise durable `scpsl-ops` activations. LocalAdmin owns the activation record,
restart timing, readiness checks, and the single automatic rollback. It does not read checkpoint
manifests or copy deployment files. Those operations stay in the fixed `scpsl-ops internal
activation` commands, so there is only one checkpoint implementation.

Managed activation is disabled when `ops_lane` is empty. To enable it, set all three values in the
LocalAdmin config:

```text
ops_lane: oa/prod
ops_state_root: /var/lib/scpsl-ops
ops_cli_path: /usr/local/bin/scpsl-ops
```

`ops_state_root` and `ops_cli_path` must be absolute. The default state root is
`/var/lib/scpsl-ops` on Linux and `C:\ProgramData\scpsl-ops` on Windows. Managed activation must
never leave the server worse off than stock LocalAdmin. It never keeps the game from starting or
stops a running game on its own: a missing record (first setup), a malformed or unsupported record,
an invalid ops configuration, or any store or hook error is reported as needing repair, and the game
starts with the files on disk. LabAPI skips any plugin that fails to load. The only restarts it
causes are the activation edge an operator queued and one rollback restart of a candidate with
evidence of failure. File hooks that run while the game is stopped get 60 seconds each; staging
cleanup runs after the game starts. `scpsl-ops` bootstraps the initial verified record while the
game runs.

The record lives at `lanes/<host>/<lane>/activation.json` below the state root. LocalAdmin writes a
complete temporary file, flushes it, and atomically replaces the record. Every write checks and
increments the record revision. A LocalAdmin restart resumes `pending`, `activating`, `verifying`,
or `rolling-back` from that file.

Readiness requires the child process, console connection, configured heartbeat, configured UDP
listener, and a correlated `players` response. When LocalAdmin's heartbeat monitoring is disabled
(`enable_heartbeat: false` or `hbctrl disable`), readiness does not wait for a heartbeat it cannot
observe and records a passing advisory `startup-heartbeat` check instead. When a poll cannot read
the UDP listener table, readiness does not wait on it and records a passing advisory `udp` check
with the reason; only a readable table without the listener keeps readiness waiting. The probe
accepts a valid zero-player response and never has two `players` requests in flight. The runtime
hook adds managed-file, loader, and server verification checks.

LocalAdmin rolls a candidate back only on evidence: an unplanned child exit during readiness, no
readiness answer before the deadline while monitoring works, or a `verify-runtime` result with at
least one failed check. It then restores the previous checkpoint once. A shutdown or restart chosen
by an operator or the game keeps its action, and verification resumes in the next session.

A verification tooling failure is not evidence. When `verify-runtime` cannot start, times out,
crashes, answers invalid or mismatched JSON, or fails without a failed check (for example checkpoint
finalization on a full disk, or a missing server-verification proof), nothing is restarted or rolled
back: the candidate keeps running, the record stays `verifying`, LocalAdmin logs a red line, and a
later crash follows the normal crash handling. Verification retries every 60 seconds for up to 30
minutes, each attempt with a fresh readiness deadline; a retry rolls back only on a failed check. A
successful retry records `verified`. If the retries run out, the record stays `verifying` and the
next session (the next game restart) verifies again.

If the previous checkpoint also fails readiness or cannot be verified, or the rollback files cannot
be applied, the record enters `recovery-required` with the lock and evidence intact, and the game
keeps running on whatever is installed. There is no rollback loop and no further restart. A child
that dies is relaunched by the normal crash handling. After repair,
`scpsl-ops recovery adopt` records the repaired checkpoint without restarting the game. A forced
restart (`restart`, `forcerestart`, or the stalled-restart fallback) applies a queued activation
instead of leaving it for a second restart.

### Restart edges of a pending activation

A pending activation waits for one of two edges, matching the operator's `scpsl-ops` request:

| Edge | What LocalAdmin does |
|---|---|
| `round-end` (`-rnr`) | Arms the game's native restart-next-round flag through the tracker above, so the game itself restarts after the current round. The `safe` intent stays armed as a fallback and restarts early when the game reports idle mode (an empty server), without polling. |
| `immediate` (`--now`) | Sends native `rnr` (only if not armed) then `restart`: the round restarts and the game exits at once. |

Any game restart exit (native `rnr`, `restart_after_rounds`, or LocalAdmin's own sequence) commits a
pending `round-end`/`immediate` activation to `activating`, so the relaunch applies the staged
checkpoint. A crash exit never commits; the next session re-arms the pending record. An `empty`-edge
activation ignores unrelated restarts and keeps waiting.

`ops <request-id> reschedule <activation-id> <expected-revision> <immediate|round-end>` moves a
pending activation to the other edge without re-staging: the record's `restart` is updated first,
then `immediate` fires the sequence right away. `scpsl-ops pending edge <lane> --now` is the public
form. While an activation is pending, `restartwhen` refuses a separate intent and names that command.
Cancelling a pending activation also disarms the native flag when LocalAdmin armed it.

The existing command channel carries the private machine protocol. Requests begin with `ops` and a
UUID. Responses are one compact JSON line prefixed with `@@SCPSL_OPS_V1@@`, so a client can ignore
unrelated console output. Operators should use `scpsl-ops`; the raw `ops` command is an integration
surface, not a second user interface.

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
| --game-state-root [absolute path] | | Sets `SCPSL_OPS_STATE_ROOT` for the game process. Use a distinct path for each server port when plugins require it. |
| --prepend-path [absolute directory] | | Prepends a directory to `PATH` for LocalAdmin and its game child, for tools such as FFmpeg. |
| --restartsLimit |  | Specifies a limit of auto-restarts in a specified time window.<br>Setting this argument to 0 disables auto-restarts.<br>Setting this argument to -1 disables the limit.<br>*Default value: 4* |
| --restartsTimeWindow |  | Specifies a time window (in seconds) for the auto-restarts limit.<br>Setting this argument to 0 disables resetting the amount of auto-restarts after a specified amount of time.<br>*Default value: 480* |
| --logLengthLimit |  | Specifies the limit of characters in LocalAdmin log file.<br>Suffixes `k`, `M`, `G` and `T` are supported, eg. `5G` is equal to `5000000000` characters.<br>Setting this argument to 0 disables the limit.<br>*Default value: 25G* |
| --logEntriesLimit |  | Specifies the limit of entries in LocalAdmin log file.<br>Suffixes `k`, `M`, `G` and `T` are supported, eg. `5G` is equal to `5000000000` entries.<br>Setting this argument to 0 disables the limit.<br>*Default value: 10G* |
| --acceptEULA | | Accepts the [End User License Agreement (EULA)](https://link.scpslgame.com/eula) without prompting. Alternatively, you can set `ACCEPT_SCPSL_EULA=TRUE` in environment variables to automatically accept the [EULA](https://link.scpslgame.com/eula). |
