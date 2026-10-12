namespace LocalAdmin.V2.Commands.Meta;

internal abstract class CommandBase
{
    public readonly string Name;
    public readonly string Description;
    public readonly bool SendToGame;

    protected CommandBase(string name, bool sendToGame = false)
    {
        Name = name.ToUpperInvariant();
        Description = "No Description Provided";
        SendToGame = sendToGame;
    }

    protected CommandBase(string name, string description, bool sendToGame = false)
    {
        Name = name.ToUpperInvariant();
        Description = description;
        SendToGame = sendToGame;
    }

    /// <summary>
    /// True for machine-polled commands whose input echo goes to command-channel clients only,
    /// so a poll every few seconds does not grow the console window or the LocalAdmin log.
    /// </summary>
    internal virtual bool EchoToChannelOnly => false;

    internal abstract void Execute(string[] arguments);
}