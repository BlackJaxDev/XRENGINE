namespace XREngine.Rendering;

/// <summary>Identifies a backend that validates and binds source triangle UDIM coverage.</summary>
public interface IAuthoredUdimTriangleCoverageBackendCapability
{
    /// <summary>Gets whether fragment primitive identity is enabled on this device.</summary>
    bool SupportsUdimTriangleCoverage { get; }
}
