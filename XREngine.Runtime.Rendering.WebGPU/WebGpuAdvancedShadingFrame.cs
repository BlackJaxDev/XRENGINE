namespace XREngine.Rendering.WebGPU;

/// <summary>Completion-slot-owned classification buffers; counts and dispatch dimensions remain GPU-owned.</summary>
internal sealed class WebGpuAdvancedShadingFrame : IDisposable
{
    internal const int MaximumCohorts = WebGpuAdvancedMaterialContract.MaximumCohorts;
    internal const int MaximumRetainedCohorts = MaximumCohorts * (int)AdvancedFrameSlotContract.DefaultSlotCount *
        WebGpuRendererHost.MaximumAdvancedOutputFamilies;
    internal WebGpuAdvancedShadingFrame(WebGpuRendererHost renderer)
    {
        Materials = new(renderer, "Advanced material cohort map");
        Tiles = new(renderer, "Advanced GPU cohort tiles");
        Counts = new(renderer, "Advanced GPU cohort counts");
        Arguments = new(renderer, "Advanced GPU shade arguments", BrowserBufferUsage.Indirect);
    }
    internal readonly WebGpuOwnedStorageBuffer Materials;
    internal readonly WebGpuOwnedStorageBuffer Tiles;
    internal readonly WebGpuOwnedStorageBuffer Counts;
    internal readonly WebGpuOwnedStorageBuffer Arguments;
    internal readonly WebGpuAdvancedShadingCohort?[] Cohorts = new WebGpuAdvancedShadingCohort?[MaximumCohorts];
    internal uint[] MaterialRows = [];
    internal int CohortCount;
    internal uint TileCapacity;
    internal uint ClassifiedSequence;
    internal ulong PreparationGeneration;
    internal ulong DatabaseEpoch;
    internal AdvancedMaterialDatabaseGenerations MaterialGenerations;
    internal AdvancedGlobalResourceDatabaseGenerations ResourceGenerations;
    internal uint ViewIndex;
    internal bool IblEnabled;
    internal uint Width;
    internal uint Height;
    public void Dispose()
    {
        Materials.Dispose(); Tiles.Dispose(); Counts.Dispose(); Arguments.Dispose();
        foreach (WebGpuAdvancedShadingCohort? cohort in Cohorts) cohort?.Dispose();
    }
}
