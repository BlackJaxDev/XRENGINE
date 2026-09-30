using Valve.VR;

namespace XREngine;

/// <summary>Exposes the initialized OpenVR system owned by the native backend.</summary>
public sealed class RuntimeOpenVrApi
{
    public bool IsHeadsetPresent => CVR is not null;
    public CVRSystem? CVR { get; set; }
}
