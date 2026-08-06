using LocalAdmin.V2.Core.CommandChannel;
using System;
using System.Threading.Tasks;

namespace LocalAdmin.V2.Core;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // "ctl" is an explicit, self-contained client mode. Check it before initializing any
        // server state or reading laargs.txt, so an SSH command cannot accidentally start a game.
        if (CommandChannelClientCli.IsInvocation(args))
            return await CommandChannelClientCli.RunAsync(args, Console.Out, Console.Error);

        Utf8Json.Resolvers.CompositeResolver.RegisterAndSetAsDefault(
            Utf8Json.Resolvers.GeneratedResolver.Instance,
            Utf8Json.Resolvers.BuiltinResolver.Instance
        );

        while (true)
        {
            using var la = new LocalAdmin();
            await la.Start(StartupArgManager.MergeStartupArgs(args));
        }
        // ReSharper disable once FunctionNeverReturns
    }
}
