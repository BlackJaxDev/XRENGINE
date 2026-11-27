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
    bool framebufferYDown,
    bool hasTightFrustum = false,
    float angleLeft = 0.0f,
    float angleRight = 0.0f,
    float angleUp = 0.0f,
    float angleDown = 0.0f,
    float nearZ = 0.1f,
    float farZ = 1000.0f)
{
    public XRCamera SourceCamera { get; } = sourceCamera;
    public ulong SourceCameraIdentity { get; } = sourceCameraIdentity;
    public Matrix4x4 ReflectedWorld { get; } = reflectedWorld;
    public Vector3 PlanePoint { get; } = planePoint;
    public Vector3 PlaneNormal { get; } = planeNormal;
    public bool FramebufferYDown { get; } = framebufferYDown;
    public bool HasTightFrustum { get; } = hasTightFrustum;
    public float AngleLeft { get; } = angleLeft;
    public float AngleRight { get; } = angleRight;
    public float AngleUp { get; } = angleUp;
    public float AngleDown { get; } = angleDown;
    public float NearZ { get; } = nearZ;
    public float FarZ { get; } = farZ;
}
