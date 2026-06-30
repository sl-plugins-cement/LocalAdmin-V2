using LocalAdmin.V2.Commands.Meta;

namespace LocalAdmin.V2.Commands;

internal sealed class RestartCommand : CommandBase
{
    public RestartCommand() : base("Restart", "Restarts the server.") { }

    internal override void Execute(string[] arguments)
    {
        var localAdmin = Core.LocalAdmin.Singleton!;

        localAdmin.ClaimExitAction(Core.LocalAdmin.ShutdownAction.Restart);

        if (localAdmin.Server is { Connected: true } && localAdmin.Server.WriteLine("exit"))
            return;

        localAdmin.ForceScheduledRestart("manual restart command");
    }
}
