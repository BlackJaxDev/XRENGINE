namespace XREngine.Core.Files;

/// <summary>Reports a monitor failure through a managed exception.</summary>
public sealed class AssetMonitorErrorEventArgs(Exception exception) : EventArgs
{
    public Exception GetException() => exception;
}
