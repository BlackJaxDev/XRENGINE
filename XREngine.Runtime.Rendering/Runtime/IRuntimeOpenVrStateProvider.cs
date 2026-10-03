using System.Numerics;

namespace XREngine;

/// <summary>Supplies OpenVR tracking values without exposing the native runtime to rendering state.</summary>
public interface IRuntimeOpenVrStateProvider
{
    float RealWorldIpd { get; }

    /// <summary>Gets the runtime's asymmetric projection for an eye without initializing a runtime.</summary>
    bool TryGetEyeProjectionMatrix(bool leftEye, float nearPlane, float farPlane, out Matrix4x4 projection);
}
