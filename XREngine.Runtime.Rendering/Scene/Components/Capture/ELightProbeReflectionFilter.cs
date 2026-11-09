namespace XREngine.Components.Capture.Lights;

/// <summary>Selects the producer used to filter reflection probe radiance.</summary>
public enum ELightProbeReflectionFilter
{
    /// <summary>Uses importance-sampled GGX convolution.</summary>
    Ggx,

    /// <summary>Uses the recursive cube filter of Unity BuiltIn realtime probes.</summary>
    UnityRealtime,
}
