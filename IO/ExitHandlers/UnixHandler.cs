#if LINUX_SIGNALS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace LocalAdmin.V2.IO.ExitHandlers
{
    /// <summary>
    ///     Native signal processing on Unix systems.
    /// </summary>
    internal sealed class UnixHandler : IExitHandler
    {
        public static readonly UnixHandler Handler = new UnixHandler();
        private readonly List<PosixSignalRegistration> _registrations = new();
        private int _handled;

        public void Setup()
        {
            Register(PosixSignal.SIGINT);
            Register(PosixSignal.SIGTERM);
            Register(PosixSignal.SIGHUP);
        }

        private void Register(PosixSignal signal)
        {
            _registrations.Add(PosixSignalRegistration.Create(signal, context =>
            {
                // Prevent the runtime's default termination so LocalAdmin can ask the game to
                // exit and finish its normal teardown. systemd still has SIGKILL as a bounded
                // fallback if that graceful path stalls.
                context.Cancel = true;

                if (Interlocked.Exchange(ref _handled, 1) != 0)
                    return;

                if (Core.LocalAdmin.Singleton == null)
                    Environment.Exit(0);
                else
                    Core.LocalAdmin.HandleExitSignal();
            }));
        }
    }
}
#endif
