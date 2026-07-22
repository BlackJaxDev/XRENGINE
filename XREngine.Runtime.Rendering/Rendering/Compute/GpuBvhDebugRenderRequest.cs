using System.Numerics;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Captures one GPU BVH overlay request until the pipeline reaches its late-debug pass.
/// </summary>
internal readonly record struct GpuBvhDebugRenderRequest(
    GpuBvhDebugLineRenderer Renderer,
    XRDataBuffer NodeBuffer,
    uint NodeCount,
    Matrix4x4 NodeToWorld,
    uint MaxNodes,
    float LineWidth,
    Vector4 LeafColor,
    Vector4 InternalColor,
    uint ShowFilter,
    GpuBvhDebugNodeClassOptions? NodeClasses);
