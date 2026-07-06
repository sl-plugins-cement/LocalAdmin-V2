# Enhanced LocalAdmin fork SSH-side command-channel client.
# Licensed under The MIT License (see repository LICENSE file).
#
# Opens the LocalAdmin command-channel named pipe, sends ONE command line, and streams
# back the console output frames (the command's reply) until the channel goes quiet.
#
# This works against a DETACHED / headless LocalAdmin, unlike the old
# AttachConsole + WriteConsoleInput approach (which fails headless with ERROR_INVALID_HANDLE).
#
# Usage:
#   pwsh -File la-send.ps1 -Port 7901 -Command "players"
#   pwsh -File la-send.ps1 -Port 7901 -Command "players" -QuietMs 800 -TimeoutMs 8000
#
# The pipe name is "<BaseName>-<Port>" matching command_channel_pipe_name in the LA config.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [int]$Port,

    [Parameter(Mandatory = $true)]
    [string]$Command,

    [string]$BaseName = "LocalAdmin-Console",

    # Override the computed pipe name entirely (advanced; bypasses the SID suffix logic).
    [string]$PipeNameOverride = "",

    # Pipe server host. "." = local machine.
    [string]$Server = ".",

    # How long to wait for the initial connection.
    [int]$ConnectTimeoutMs = 5000,

    # Stop reading once no new line has arrived for this many ms (the reply has settled).
    # A command's reply round-trips game -> LocalAdmin -> pipe, so allow a generous gap.
    [int]$QuietMs = 1500,

    # Hard cap on total read time.
    [int]$TimeoutMs = 12000
)

$ErrorActionPreference = "Stop"

# The server names the pipe "<BaseName>-<Port>-<currentUserSID>" on Windows. The SID suffix
# reduces cross-account collisions, but it is not a hardened anti-squatting secret; the pipe
# DACL is the access control. We run as the same user as LocalAdmin, so we reconstruct the
# identical name from our own SID. On non-Windows the server omits the SID. An explicit
# -PipeNameOverride wins if supplied.
if ($PipeNameOverride) {
    $pipeName = $PipeNameOverride
}
elseif ($IsWindows -or ($null -eq $IsWindows)) {
    # $IsWindows is $null on Windows PowerShell 5.1 (always Windows there).
    $sid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
    $pipeName = "$BaseName-$Port-$sid"
}
else {
    $pipeName = "$BaseName-$Port"
}

# CurrentUserOnly matches the server: on non-Windows it restricts the socket to this user, and on
# Windows it verifies the pipe server runs as the same user (defense against a squatted pipe). It
# was added in .NET Framework 4.7.2 / .NET Core 2.1, so gate on it for older Windows PowerShell 5.1
# hosts (which would otherwise fail resolving the enum member).
$pipeOptions = [System.IO.Pipes.PipeOptions]::Asynchronous
if ([enum]::IsDefined([System.IO.Pipes.PipeOptions], 'CurrentUserOnly')) {
    $pipeOptions = $pipeOptions -bor ([System.IO.Pipes.PipeOptions]::CurrentUserOnly)
}

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
    $Server,
    $pipeName,
    [System.IO.Pipes.PipeDirection]::InOut,
    $pipeOptions)

try {
    try {
        $pipe.Connect($ConnectTimeoutMs)
    } catch {
        Write-Error "Could not connect to pipe '$pipeName' within ${ConnectTimeoutMs}ms. Is LocalAdmin running on port $Port with the command channel enabled? ($($_.Exception.Message))"
        exit 2
    }

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $reader = New-Object System.IO.StreamReader($pipe, $utf8, $false, 1024, $true)
    $writer = New-Object System.IO.StreamWriter($pipe, $utf8, 1024, $true)
    $writer.AutoFlush = $true
    $writer.NewLine = "`n"

    # Send the command line.
    $writer.WriteLine($Command)
    $writer.Flush()

    # Read reply frames until the channel goes quiet or the hard timeout elapses.
    # IMPORTANT: only one ReadLineAsync may be in flight on a stream at a time, so we hold a
    # single pending task across poll iterations and only start a new read once it completes.
    #
    # LocalAdmin echoes every processed input line as ">>> <command>". We ANCHOR on our own echo:
    # lines before it (background console output, or another concurrent client's reply) are
    # discarded, and only the output after our echo is treated as our command's reply. If the echo
    # is never seen (older server), we fall back to emitting everything we captured.
    $echoMarker = ">>> " + $Command.Trim()
    $overall = [System.Diagnostics.Stopwatch]::StartNew()
    $lastActivity = [System.Diagnostics.Stopwatch]::StartNew()
    $anchored = $false
    $gotReply = $false
    $preAnchor = New-Object System.Collections.Generic.List[string]
    $slice = [Math]::Min(150, [Math]::Max(50, $QuietMs))
    $readTask = $reader.ReadLineAsync()

    while ($overall.ElapsedMilliseconds -lt $TimeoutMs) {
        if ($readTask.Wait($slice)) {
            $line = $readTask.Result
            if ($null -eq $line) {
                break  # pipe closed by server
            }
            $lastActivity.Restart()
            if (-not $anchored) {
                if ($line.Trim() -eq $echoMarker) {
                    $anchored = $true  # our command's echo; the reply follows
                }
                else {
                    $preAnchor.Add($line)
                }
            }
            else {
                Write-Output $line
                $gotReply = $true
            }
            $readTask = $reader.ReadLineAsync()  # start the next read only after this one finished
        }
        elseif ($anchored -and $lastActivity.ElapsedMilliseconds -ge $QuietMs) {
            break  # our reply has settled (a read is still pending; we just abandon it on dispose)
        }
    }

    if (-not $anchored) {
        # Never saw our echo: fall back to emitting whatever the channel sent.
        foreach ($bufferedLine in $preAnchor) {
            Write-Output $bufferedLine
            $gotReply = $true
        }
    }

    if (-not $gotReply -and -not $anchored) {
        Write-Error "Connected to '$pipeName' but received no output within timeout. (Command may have produced no reply, or the channel is busy.)"
        exit 3
    }

    if (-not $gotReply -and $anchored) {
        # The command was delivered (we saw its echo) but nothing followed within the quiet
        # window - most likely it simply produces no console reply. Say so instead of silently
        # printing nothing; still exit 0 because delivery succeeded.
        Write-Warning "Command was delivered (echo seen) but produced no output within ${QuietMs}ms."
    }
}
finally {
    try { $pipe.Dispose() } catch { }
}
