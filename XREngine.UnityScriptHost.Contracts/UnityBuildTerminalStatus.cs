namespace XREngine.UnityScriptHost.Contracts;

/// <summary>
/// Reports the terminal outcome of worker work only.
/// </summary>
public enum UnityBuildTerminalStatus
{
    /// <summary>The worker has not completed.</summary>
    NotRun,
    /// <summary>All worker stages completed.</summary>
    Succeeded,
    /// <summary>A worker stage failed.</summary>
    Failed,
    /// <summary>The worker was canceled.</summary>
    Canceled,
    /// <summary>A required operation has no executable implementation.</summary>
    UnsupportedOperation
}
