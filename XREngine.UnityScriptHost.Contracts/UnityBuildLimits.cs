namespace XREngine.UnityScriptHost.Contracts;

/// <summary>
/// Sets the upper bounds that the parent must enforce for one build.
/// </summary>
public sealed record UnityBuildLimits
{
    /// <summary>Gets the execution time limit in milliseconds.</summary>
    public long MaxDurationMilliseconds { get; init; }

    /// <summary>Gets the worker memory limit in bytes.</summary>
    public long MaxMemoryBytes { get; init; }

    /// <summary>Gets the total worker output limit in bytes.</summary>
    public long MaxOutputBytes { get; init; }

    /// <summary>Gets the maximum number of processed records.</summary>
    public int MaxGraphRecords { get; init; }

    /// <summary>Gets the maximum graph size in bytes.</summary>
    public long MaxGraphBytes { get; init; }

    /// <summary>Gets the maximum number of blobs.</summary>
    public int MaxBlobCount { get; init; }

    /// <summary>Gets the maximum size of one blob in bytes.</summary>
    public long MaxBlobBytes { get; init; }

    /// <summary>Gets the maximum number of returned diagnostics.</summary>
    public int MaxDiagnostics { get; init; }
}
