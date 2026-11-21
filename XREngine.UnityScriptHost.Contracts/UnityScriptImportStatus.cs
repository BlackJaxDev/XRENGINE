namespace XREngine.UnityScriptHost.Contracts;

/// <summary>
/// Reports the parent conversion and publication outcome.
/// </summary>
public enum UnityScriptImportStatus
{
    /// <summary>The parent has not converted the worker output.</summary>
    NotRun,
    /// <summary>The parent published a native generation.</summary>
    Published,
    /// <summary>The parent rejected conversion or publication.</summary>
    Rejected
}
