using LocalAdmin.V2.Commands.Meta;

namespace LocalAdmin.V2.Commands;

internal sealed class ForceRestartCommand : CommandBase
{
    public ForceRestartCommand() : base("Forcerestart", "Kills and restarts the server.") { }

    internal override void Execute(string[] arguments)
    {
        var localAdmin = Core.LocalAdmin.Singleton!;
        localAdmin.ClaimExitAction(Core.LocalAdmin.ShutdownAction.Restart);
        localAdmin.Exit(0, restart: true);
    }
}
