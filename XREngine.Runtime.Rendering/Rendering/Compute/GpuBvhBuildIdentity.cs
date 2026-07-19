using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Allocation-free identity of the inputs that produced a completed GPU BVH build.
/// </summary>
internal readonly record struct GpuBvhBuildIdentity(
    XRDataBuffer? AabbBuffer,
    uint PrimitiveCount,
    Vector3 SceneMin,
    Vector3 SceneMax)
{
    public bool Matches(XRDataBuffer aabbBuffer, uint primitiveCount, in Vector3 sceneMin, in Vector3 sceneMax)
        => ReferenceEquals(AabbBuffer, aabbBuffer) &&
           PrimitiveCount == primitiveCount &&
           SceneMin == sceneMin &&
           SceneMax == sceneMax;
}
