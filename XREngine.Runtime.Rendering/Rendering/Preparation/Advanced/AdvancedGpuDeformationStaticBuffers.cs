using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// One immutable aggregate-deformation source-buffer generation. Replaced
/// generations stay alive until every frame that references them completes.
/// </summary>
internal sealed class AdvancedGpuDeformationStaticBuffers
{
    public AdvancedGpuDeformationStaticBuffers(
        uint sourceCapacity,
        uint influenceCapacity,
        uint spillCapacity,
        uint rangeCapacity,
        uint recordCapacity,
        uint deltaCapacity)
    {
        SourceVertices = Create<AdvancedDeformedVertex>(
            "AdvancedDeformation.SourceVertices",
            sourceCapacity);
        SkinInfluences = Create<AdvancedSkinInfluence>(
            "AdvancedDeformation.SkinInfluences",
            influenceCapacity);
        SpillInfluences = Create<AdvancedSpillInfluence>(
            "AdvancedDeformation.SpillInfluences",
            spillCapacity);
        BlendshapeRanges = Create<AdvancedBlendshapeRange>(
            "AdvancedDeformation.BlendshapeRanges",
            rangeCapacity);
        BlendshapeRecords = Create<AdvancedBlendshapeSparseRecord>(
            "AdvancedDeformation.BlendshapeRecords",
            recordCapacity);
        BlendshapeDeltas = Create<Vector4>(
            "AdvancedDeformation.BlendshapeDeltas",
            deltaCapacity);
        InverseBindMatrices = Create<SkinPaletteMatrix>(
            "AdvancedDeformation.InverseBindMatrices",
            1u);
        InverseBindMatrices.Set(0u, SkinPaletteMatrix.Identity);
        InverseBindMatrices.CommitDirtyElements(0u, 1u);
    }

    public XRDataBuffer<AdvancedDeformedVertex> SourceVertices { get; }
    public XRDataBuffer<AdvancedSkinInfluence> SkinInfluences { get; }
    public XRDataBuffer<AdvancedSpillInfluence> SpillInfluences { get; }
    public XRDataBuffer<AdvancedBlendshapeRange> BlendshapeRanges { get; }
    public XRDataBuffer<AdvancedBlendshapeSparseRecord> BlendshapeRecords { get; }
    public XRDataBuffer<Vector4> BlendshapeDeltas { get; }
    public XRDataBuffer<SkinPaletteMatrix> InverseBindMatrices { get; }

    public void Destroy()
    {
        SourceVertices.Destroy();
        SkinInfluences.Destroy();
        SpillInfluences.Destroy();
        BlendshapeRanges.Destroy();
        BlendshapeRecords.Destroy();
        BlendshapeDeltas.Destroy();
        InverseBindMatrices.Destroy();
    }

    private static XRDataBuffer<T> Create<T>(
        string name,
        uint capacity) where T : unmanaged
        => new(
            name,
            EBufferTarget.ShaderStorageBuffer,
            Math.Max(1u, capacity))
        {
            Usage = EBufferUsage.StaticDraw,
            DisposeOnPush = false,
            Resizable = false,
        };
}
