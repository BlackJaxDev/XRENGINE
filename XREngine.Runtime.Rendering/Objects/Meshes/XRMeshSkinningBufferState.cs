using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering;

/// <summary>Complete CPU-side skinning buffer state exchanged at one publication boundary.</summary>
/// <remarks>
/// This is a reference type so one volatile reference exchange publishes the complete
/// generation to readers. Its array is never mutated after construction.
/// </remarks>
public sealed record XRMeshSkinningBufferState(
    XRDataBuffer? CoreIndices,
    XRDataBuffer? CoreWeights,
    XRDataBuffer? SpillHeaders,
    XRDataBuffer? SpillEntries,
    (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] UtilizedBones,
    ESkinningShaderConvention ShaderConvention,
    SkinningInfluenceEncoding InfluenceEncoding,
    SkinningCoreIndexFormat CoreIndexFormat,
    bool HasSpillInfluences,
    int MaxSpillInfluenceCount,
    int MaxWeightCount);
