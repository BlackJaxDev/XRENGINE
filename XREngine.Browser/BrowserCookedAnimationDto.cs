namespace XREngine.Browser;

/// <summary>Bounded baked local-pose playback plus canonical packed deformation inputs.</summary>
internal sealed class BrowserCookedAnimationDto
{
    public int SchemaVersion { get; init; } = 1;
    public required string Mesh { get; init; }
    public required int[] Parents { get; init; }
    public required float[] BindPose { get; init; }
    public required float[] InverseBindMatrices { get; init; }
    public int CoreIndexFormat { get; init; } = 1;
    public required uint[] CoreIndices { get; init; }
    public required uint[] CoreWeights { get; init; }
    public uint[] SpillHeaders { get; init; } = [];
    public uint[] SpillEntries { get; init; } = [];
    public float[] Normals { get; init; } = [];
    public float[] Tangents { get; init; } = [];
    public uint[] ShapeRanges { get; init; } = [];
    public uint[] SparseRecords { get; init; } = [];
    public uint[] QuantizedDeltas { get; init; } = [];
    public float[] QuantizationMetadata { get; init; } = [];
    public int InfluenceCap { get; init; } = 259;
    public bool MaximumMorphAccumulation { get; init; }
    public float MorphWeightThreshold { get; init; } = 0.0001f;
    public required string DefaultClip { get; init; }
    public required BrowserCookedAnimationClipDto[] Clips { get; init; }
}
