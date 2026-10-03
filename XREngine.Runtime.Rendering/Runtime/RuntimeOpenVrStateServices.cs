namespace XREngine;

/// <summary>Explicit installation point for the optional OpenVR tracking backend.</summary>
public static class RuntimeOpenVrStateServices
{
    public static IRuntimeOpenVrStateProvider? Current { get; set; }
}
