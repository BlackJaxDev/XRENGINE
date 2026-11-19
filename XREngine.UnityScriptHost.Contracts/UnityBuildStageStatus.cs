namespace XREngine.UnityScriptHost.Contracts;

/// <summary>
/// Reports the state of one worker stage.
/// </summary>
public enum UnityBuildStageStatus
{
    /// <summary>The stage did not run.</summary>
    NotRun,
    /// <summary>The stage completed.</summary>
    Succeeded,
    /// <summary>The stage failed.</summary>
    Failed
}
