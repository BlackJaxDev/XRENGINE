namespace XREngine.Components.Animation;

/// <summary>Supplemental measurements collected by the playback harness for one matrix case.</summary>
public sealed class HumanoidConformanceObservation
{
    /// <summary>XRENGINE world units represented by one meter for this evaluated avatar.</summary>
    public float EngineUnitsPerMeter { get; set; }
    /// <summary>Measured ten-loop translation drift in XRENGINE world units.</summary>
    public float TenLoopDriftEngineUnits { get; set; }
    public float TenLoopDriftDegrees { get; set; }
    public HumanoidConformanceCapability ObservedCapabilities { get; set; }
    public int ObservedEventCount { get; set; } = -1;
    public int ObservedObjectReferenceBindingCount { get; set; } = -1;
    public bool? InverseKinematicsApplied { get; set; }
    public bool? InverseKinematicsDisabled { get; set; }
    public int ObservedFootContactCount { get; set; } = -1;
    public List<string> ExplicitFailures { get; set; } = [];
    public List<string> UnobservedRelevantFields { get; set; } = [];
}
