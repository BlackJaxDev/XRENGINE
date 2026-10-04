using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Output-local work storage reused only with its canonical scene slot's completion receipt.</summary>
internal sealed class WebGpuAdvancedVisibilityFrame : IDisposable
{
    internal const int MaximumBuckets = 64;
    internal const int MaximumRetainedBuckets = MaximumBuckets * (int)AdvancedFrameSlotContract.DefaultSlotCount *
        WebGpuRendererHost.MaximumAdvancedOutputFamilies;
    internal const int MaximumRetainedRasterStates = 12 * WebGpuRendererHost.MaximumAdvancedOutputFamilies;
    internal const int MaximumCpuDraws = 1024;
    internal const int MaximumRetainedDirectCommands = MaximumCpuDraws * (int)AdvancedFrameSlotContract.DefaultSlotCount *
        WebGpuRendererHost.MaximumAdvancedOutputFamilies;
    private readonly WebGpuAdvancedCpuDraw[] _retainedCpuDraws = new WebGpuAdvancedCpuDraw[MaximumCpuDraws];
    private readonly WebGpuAdvancedVisibilityBucketKey[] _retainedBucketKeys = new WebGpuAdvancedVisibilityBucketKey[MaximumBuckets];
    private int _retainedCpuDrawCount;
    private int _retainedTopologyBucketCount;
    private bool _retainedReversedDepth;
    internal WebGpuAdvancedVisibilityFrame(WebGpuRendererHost renderer, int slot, WebGpuAdvancedUberRasterFrame uberRaster)
    {
        Payloads = new(renderer, $"Advanced visibility payloads {slot}");
        Candidates = new(renderer, $"Advanced visibility candidates {slot}");
        Producers = new(renderer, $"Advanced visibility producers {slot}");
        Triangles = new(renderer, $"Advanced compact triangles {slot}");
        Arguments = new(renderer, $"Advanced triangle arguments {slot}", BrowserBufferUsage.Indirect);
        PreparedDeformations = new(renderer, $"Advanced prepared temporal relations {slot}");
        NativeVertices = new(renderer, slot);
        UberRaster = uberRaster;
    }
    internal WebGpuAdvancedUberRasterFrame UberRaster { get; }
    internal bool HasUberRaster;
    internal WebGpuOwnedStorageBuffer Payloads { get; }
    internal WebGpuOwnedStorageBuffer Candidates { get; }
    internal WebGpuOwnedStorageBuffer Producers { get; }
    internal WebGpuOwnedStorageBuffer Triangles { get; }
    internal WebGpuOwnedStorageBuffer Arguments { get; }
    internal WebGpuOwnedStorageBuffer PreparedDeformations { get; }
    internal WebGpuAdvancedNativeVertexFrame NativeVertices { get; }
    internal WebGpuOwnedStorageBuffer GeometryArena => NativeVertices.HasWork ? NativeVertices.Geometry : Scene!.GeometryArena;
    internal AdvancedPreparedDrawDeformationRecord[] PreparedRows = [];
    internal byte[] PreparedWrites = [];
    internal uint[] ProducerRows = [];
    internal WebGpuAdvancedCpuDraw[] CpuDraws = new WebGpuAdvancedCpuDraw[MaximumCpuDraws];
    internal int CpuDrawCount;
    internal WebGpuAdvancedVisibilityBucket[] Buckets { get; } = new WebGpuAdvancedVisibilityBucket[MaximumBuckets];
    internal readonly WebGpuAdvancedVisibilitySampling?[] Sampling = new WebGpuAdvancedVisibilitySampling?[MaximumBuckets];
    internal int BucketCount;
    internal int RetainedRasterBucketCount;
    internal ulong RasterCacheRevision;
    internal uint PayloadCount;
    internal uint FrameSequence;
    internal ulong PreparationGeneration;
    internal AdvancedViewRecord View;
    internal WebGpuAdvancedSceneSlot? Scene;
    internal AdvancedGpuDeformationPublication Deformation;
    internal uint CurrentDeformationBytes;
    internal uint PreviousDeformationBytes;

    /// <summary>Changes command ownership only when direct extents or immutable raster topology change.</summary>
    internal void CommitRasterTopology(bool reversedDepth)
    {
        bool changed = CpuDrawCount != _retainedCpuDrawCount || BucketCount != _retainedTopologyBucketCount ||
            reversedDepth != _retainedReversedDepth;
        for (int index = 0; !changed && index < BucketCount; index++)
            changed = Buckets[index].Key != _retainedBucketKeys[index];
        for (int index = 0; !changed && index < CpuDrawCount; index++)
            changed = CpuDraws[index].VertexCount != _retainedCpuDraws[index].VertexCount ||
                CpuDraws[index].BucketIndex != _retainedCpuDraws[index].BucketIndex;
        if (!changed) return;
        RasterCacheRevision = checked(RasterCacheRevision + 1);
        for (int index = 0; index < BucketCount; index++) _retainedBucketKeys[index] = Buckets[index].Key;
        CpuDraws.AsSpan(0, CpuDrawCount).CopyTo(_retainedCpuDraws);
        _retainedCpuDrawCount = CpuDrawCount;
        _retainedTopologyBucketCount = BucketCount;
        _retainedReversedDepth = reversedDepth;
    }

    public void Dispose()
    {
        Payloads.Dispose();
        Candidates.Dispose();
        Producers.Dispose();
        Triangles.Dispose();
        Arguments.Dispose();
        PreparedDeformations.Dispose();
        NativeVertices.Dispose();
        foreach (WebGpuAdvancedVisibilitySampling? sampling in Sampling) sampling?.Dispose();
    }
}
