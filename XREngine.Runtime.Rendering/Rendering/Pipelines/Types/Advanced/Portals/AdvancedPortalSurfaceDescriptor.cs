using System.Numerics;
using XREngine.Components.Lights;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Immutable per-frame snapshot of one active planar mirror or portal surface supplied
/// to <see cref="AdvancedPortalViewScheduler"/> without heap allocation.
/// </summary>
public readonly record struct AdvancedPortalSurfaceDescriptor(
    ulong ProviderId,
    AdvancedGpuHandle DrawHandle,
    Matrix4x4 WorldMatrix,
    Matrix4x4 PreviousWorldMatrix,
    Vector3 PlanePoint,
    Vector3 PlaneNormal,
    Vector3 PreviousPlanePoint,
    Vector3 PreviousPlaneNormal,
    Vector3 QuadCorner0,
    Vector3 QuadCorner1,
    Vector3 QuadCorner2,
    Vector3 QuadCorner3,
    Vector4 BoundsSphere,
    Vector4 GlassTint,
    float FresnelStrength,
    float SpecularStrength,
    uint CullingLayerMask,
    EMirrorMsaaShadingMode MsaaShadingMode,
    bool AllowRecursion,
    bool RenderTransparents,
    bool IsMirror,
    Matrix4x4 PortalTransform,
    Matrix4x4 PreviousPortalTransform,
    bool TransparentBackground = false);
