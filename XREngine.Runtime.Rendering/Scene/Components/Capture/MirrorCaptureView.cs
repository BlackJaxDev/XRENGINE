using System.Numerics;
using XREngine.Rendering;

namespace XREngine.Components.Lights;

/// <summary>Exact source-camera state admitted for one mirror capture slot.</summary>
public readonly record struct MirrorCaptureView(
    XRCamera sourceCamera,
    ulong sourceCameraIdentity,
    Matrix4x4 reflectedWorld,
    Vector3 planePoint,
    Vector3 planeNormal,
    bool framebufferYDown)
{
    public XRCamera SourceCamera { get; } = sourceCamera;
    public ulong SourceCameraIdentity { get; } = sourceCameraIdentity;
    public Matrix4x4 ReflectedWorld { get; } = reflectedWorld;
    public Vector3 PlanePoint { get; } = planePoint;
    public Vector3 PlaneNormal { get; } = planeNormal;
    public bool FramebufferYDown { get; } = framebufferYDown;
}
