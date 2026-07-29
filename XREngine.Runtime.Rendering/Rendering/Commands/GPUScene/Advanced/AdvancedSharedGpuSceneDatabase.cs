namespace XREngine.Rendering.Commands;

/// <summary>
/// Pipeline-neutral owner of the canonical scene/material records and their
/// shared logical-handle lookup image. Desktop and eye pipelines consume this
/// data independently while retaining separate outputs and temporal histories.
/// </summary>
public sealed class AdvancedSharedGpuSceneDatabase
{
    public AdvancedSharedGpuSceneDatabase(
        in AdvancedSharedGpuSceneCapacityProfile capacities)
    {
        Scene = new AdvancedGpuSceneDatabase(capacities.Scene);
        Materials = new AdvancedMaterialDatabase(
            capacities.MaterialRecords,
            capacities.ShadingKernels,
            capacities.MaterialLayouts,
            capacities.MaterialLayoutMembers,
            capacities.MaterialConstantWords,
            capacities.MaterialTextureBindings);
        HandleLookups = new AdvancedGpuSceneLookupTable(Scene, Materials);
    }

    public AdvancedGpuSceneDatabase Scene { get; }

    public AdvancedMaterialDatabase Materials { get; }

    public AdvancedGpuSceneLookupTable HandleLookups { get; }

    public bool TryResolveDraw(
        AdvancedGpuHandle drawHandle,
        out AdvancedResolvedSharedDrawRecords resolved)
    {
        resolved = default;
        if (!Scene.TryResolveDraw(drawHandle, out AdvancedResolvedDrawRecords scene) ||
            !Materials.Materials.TryGet(scene.Draw.Material, out AdvancedMaterialRecord material))
        {
            return false;
        }

        resolved.Scene = scene;
        resolved.Material = material;
        return true;
    }

    public bool TryCreateDrawDependencySnapshot(
        AdvancedGpuHandle drawHandle,
        out AdvancedSharedDrawDependencySnapshot snapshot)
    {
        snapshot = default;
        if (!Scene.TryCreateDrawDependencySnapshot(
                drawHandle,
                out AdvancedDrawDependencySnapshot scene) ||
            !Materials.Materials.TryGetDenseIndex(
                scene.Material,
                out uint materialDenseIndex))
        {
            return false;
        }

        snapshot = new AdvancedSharedDrawDependencySnapshot(
            scene,
            materialDenseIndex);
        return true;
    }

    public void BeginStructuralUpdate()
    {
        Scene.BeginStructuralUpdate();
        Materials.Materials.ClearPublishedRemaps();
        Materials.Kernels.ClearPublishedRemaps();
        Materials.Layouts.ClearPublishedRemaps();
    }

    /// <summary>
    /// Compacts all physical tables, publishes remaps, and refreshes the GPU handle
    /// lookup image. Returns -1 when a retained remap batch has exhausted capacity.
    /// </summary>
    public int CompactAndPublish()
    {
        int total = Scene.CompactAndPublishRemaps();
        if (total < 0 ||
            !Accumulate(Materials.Materials.Compact(), ref total) ||
            !Accumulate(Materials.Kernels.Compact(), ref total) ||
            !Accumulate(Materials.Layouts.Compact(), ref total) ||
            !HandleLookups.Publish(Scene, Materials))
        {
            return -1;
        }

        return total;
    }

    public bool PublishHandleLookups()
        => HandleLookups.Publish(Scene, Materials);

    public void GrowAtFrameBoundary(
        in AdvancedSharedGpuSceneCapacityProfile capacities)
    {
        Scene.GrowAtFrameBoundary(capacities.Scene);
        Materials.GrowAtFrameBoundary(
            capacities.MaterialRecords,
            capacities.ShadingKernels,
            capacities.MaterialLayouts,
            capacities.MaterialLayoutMembers,
            capacities.MaterialConstantWords,
            capacities.MaterialTextureBindings);
        HandleLookups.RebuildAtFrameBoundary(Scene, Materials);
    }

    private static bool Accumulate(int result, ref int total)
    {
        if (result < 0)
            return false;

        total = checked(total + result);
        return true;
    }
}
