namespace XREngine;

/// <summary>Explicit installation point for OpenVR frame submission.</summary>
public static class RuntimeOpenVrCompositorServices
{
    public static IRuntimeOpenVrCompositor? Current { get; set; }
}
