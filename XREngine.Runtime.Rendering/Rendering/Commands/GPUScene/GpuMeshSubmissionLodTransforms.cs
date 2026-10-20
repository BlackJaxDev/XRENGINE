using System.Numerics;

namespace XREngine.Rendering.Commands;

/// <summary>Component-space history sealed with a source command before its swap callbacks run.</summary>
public readonly record struct GpuMeshSubmissionLodTransforms(
    Matrix4x4 CurrentComponentWorld,
    Matrix4x4 PreviousComponentWorld,
    bool SkinningEnabled,
    bool PreviousSkinningEnabled);
