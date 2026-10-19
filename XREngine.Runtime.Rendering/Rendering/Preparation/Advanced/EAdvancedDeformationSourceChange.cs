namespace XREngine.Rendering;

/// <summary>Changed inputs in the last completed deformation source scan.</summary>
[Flags]
public enum EAdvancedDeformationSourceChange : ulong
{
    None = 0,
    Initial = 1UL << 0,
    InterruptedScan = 1UL << 1,
    AddedMesh = 1UL << 2,
    RemovedMesh = 1UL << 3,
    Topology = 1UL << 4,
    Policy = 1UL << 5,
    Geometry = 1UL << 6,
    MorphState = 1UL << 7,
    MorphRanges = 1UL << 8,
    MorphRecords = 1UL << 9,
    MorphDeltas = 1UL << 10,
    MorphMetadata = 1UL << 11,
    SkinningState = 1UL << 12,
    SkinningIndices = 1UL << 13,
    SkinningWeights = 1UL << 14,
    SkinningSpillHeaders = 1UL << 15,
    SkinningSpillEntries = 1UL << 16,
    BlendshapeNames = 1UL << 17,
    BlendshapeCounts = 1UL << 18,
    BlendshapeIndices = 1UL << 19,
    BlendshapeDeltas = 1UL << 20,
}
