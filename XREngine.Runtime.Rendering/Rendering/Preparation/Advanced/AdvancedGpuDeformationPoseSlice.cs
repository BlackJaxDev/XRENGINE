namespace XREngine.Rendering;

/// <summary>
/// Frame-local global ranges for one shared renderer pose.
/// </summary>
public readonly record struct AdvancedGpuDeformationPoseSlice(
    uint BonePaletteOffset,
    uint BoneCount,
    uint ActiveBlendshapeOffset,
    uint ActiveBlendshapeCount,
    ulong PoseVersion,
    ulong BlendshapeVersion);
