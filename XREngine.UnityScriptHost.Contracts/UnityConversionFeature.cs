namespace XREngine.UnityScriptHost.Contracts;

/// <summary>Records one source feature and its native mapping or limitation without authorizing publication.</summary>
[Serializable]
public sealed record UnityConversionFeature
{
    /// <summary>Gets the stable feature or diagnostic identifier.</summary>
    public string FeatureId { get; init; } = string.Empty;
    /// <summary>Gets the assessment of this feature only.</summary>
    public UnityConversionFeatureStatus Status { get; init; }
    /// <summary>Gets the source assembly, when known.</summary>
    public string? Assembly { get; init; }
    /// <summary>Gets the source asset path, when known.</summary>
    public string? SourcePath { get; init; }
    /// <summary>Gets the source object identity, distinct from a runtime instance identity.</summary>
    public string? SourceObjectId { get; init; }
    /// <summary>Gets the source member, when known.</summary>
    public string? Member { get; init; }
    /// <summary>Gets the existing native type or operation used by the converter, when known.</summary>
    public string? NativeTarget { get; init; }
    /// <summary>Gets the affected user-visible function or authoring step.</summary>
    public string Functionality { get; init; } = string.Empty;
    /// <summary>Gets the evidence for the status.</summary>
    public string Reason { get; init; } = string.Empty;
    /// <summary>Gets the observed loss or approximation. Null means it has not been established.</summary>
    public string? LostFunctionality { get; init; }
    /// <summary>Gets the missing native capability. This describes separate work, not implementation authority.</summary>
    public string? RequiredNativeCapability { get; init; }
}
