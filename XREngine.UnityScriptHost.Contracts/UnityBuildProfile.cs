namespace XREngine.UnityScriptHost.Contracts;

/// <summary>
/// Identifies the compiler, API, module, and backend selection for a build.
/// </summary>
public sealed record UnityBuildProfile
{
    /// <summary>Gets the Unity API profile identifier and version.</summary>
    public string UnityApiProfile { get; init; } = string.Empty;

    /// <summary>Gets the compiler profile identifier and version.</summary>
    public string CompilerProfile { get; init; } = string.Empty;

    /// <summary>Gets the backend identifier and version.</summary>
    public string Backend { get; init; } = string.Empty;

    /// <summary>Gets the selected imported source package identifiers and versions.</summary>
    public IReadOnlyList<string> SourcePackages { get; init; } = Array.Empty<string>();
}
