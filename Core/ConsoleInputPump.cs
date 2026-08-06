// Enhanced LocalAdmin fork.
// Licensed under The MIT License (see repository LICENSE file).
// Original LocalAdmin V2 Copyright by Łukasz "zabszk" Jurczyk and KernelError, 2019 - 2026.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LocalAdmin.V2.Core;

/// <summary>
/// Owns the process-wide standard-input reader. A LocalAdmin process can run many game
/// sessions, but it must never start a new <see cref="TextReader.ReadLine"/> call while the
/// reader from the previous session is still blocked.
/// </summary>
internal sealed class ConsoleInputPump
{
    private readonly TextReader _reader;
    private readonly Action<string> _enqueue;
    private readonly Action? _onEndOfInput;
    private readonly Action<Exception>? _onError;
    private readonly object _startLock = new();

    private Task? _task;

    internal ConsoleInputPump(
        TextReader reader,
        Action<string> enqueue,
        Action? onEndOfInput = null,
        Action<Exception>? onError = null)
    {
        _reader = reader;
        _enqueue = enqueue;
        _onEndOfInput = onEndOfInput;
        _onError = onError;
    }

    /// <summary>
    /// Starts the reader exactly once. Console input has no reliably cancellable synchronous
    /// API on every supported runtime, so the one reader lives for the process lifetime on a
    /// dedicated background thread. EOF ends it permanently (the normal systemd /dev/null case).
    /// </summary>
    internal Task Start()
    {
        lock (_startLock)
        {
            return _task ??= Task.Factory.StartNew(
                Pump,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    private void Pump()
    {
        try
        {
            while (true)
            {
                var input = _reader.ReadLine();
                if (input == null)
                {
                    _onEndOfInput?.Invoke();
                    return;
                }

                if (!string.IsNullOrWhiteSpace(input))
                    _enqueue(input);
            }
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
    }
}
