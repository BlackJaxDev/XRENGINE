namespace XREngine;

/// <summary>Supplies OpenVR tracking values without exposing the native runtime to rendering state.</summary>
public interface IRuntimeOpenVrStateProvider
{
    float RealWorldIpd { get; }
}
