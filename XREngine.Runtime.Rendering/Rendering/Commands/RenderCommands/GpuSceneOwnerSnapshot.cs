using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering.Info;

namespace XREngine.Rendering.Commands;

/// <summary>Owner metadata sealed with a mesh command before swap callbacks run.</summary>
internal readonly record struct GpuSceneOwnerSnapshot(
    bool Is3D,
    uint LayerMask,
    bool CastsShadows,
    bool ReceivesShadows,
    AABB? LocalCullingVolume,
    Matrix4x4 CullingOffsetMatrix)
{
    internal static GpuSceneOwnerSnapshot CaptureLive(RenderInfo? renderInfo)
    {
        if (renderInfo is not RenderInfo3D info3D)
            return default;

        return new(true, 1u << info3D.Layer, info3D.CastsShadows,
            info3D.ReceivesShadows, info3D.LocalCullingVolume,
            info3D.CullingOffsetMatrix);
    }
}
