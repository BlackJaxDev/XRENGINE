namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Identifies a local companion source, a direct archive, or a VPM repository.
/// </summary>
public class UnityPackageDependencySettings
{
    /// <summary>
    /// Gets or sets the local path or HTTPS source URL.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the package ID in a VPM repository.
    /// </summary>
    public string? PackageId { get; set; }

    /// <summary>
    /// Gets or sets an exact VPM package version. Leave this empty to pin the greatest stable version.
    /// </summary>
    public string? Version { get; set; }
}
