using NUnit.Framework;
using Shouldly;
using System.Numerics;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Contract coverage for the scene GPU-BVH lifecycle, layout, construction,
/// traversal, and bounds-normalization architecture.
/// </summary>
[TestFixture]
public sealed class GpuBvhSceneTreeImprovementTests
{
    [Test]
    public void CompactNodeLayout_EmbedsPrimitiveRangeAtExactStd430Offsets()
    {
        string host = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTypes.cs");
        string shader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_nodes.glslinc");

        host.ShouldContain("NodeStrideScalars = 12");
        host.ShouldContain("[FieldOffset(12)] public uint LeftChild");
        host.ShouldContain("[FieldOffset(16)] public Vector3 MaxBounds");
        host.ShouldContain("[FieldOffset(32)] public uint PrimitiveStart");
        host.ShouldContain("[FieldOffset(44)] public uint Flags");
        shader.ShouldContain("BVH_NODE_STRIDE_SCALARS 12u");
        shader.ShouldNotContain("_pad0");
        shader.ShouldNotContain("BVH_RANGE_STRIDE_SCALARS");
    }

    [Test]
    public void LargeMortonSort_UsesFourPassStableRadixPipeline()
    {
        string dispatch = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Dispatch.cs");
        string scatter = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/OctreeGeneration/radix_morton_scatter.comp");

        dispatch.ShouldContain("for (uint shift = 0u; shift < 32u; shift += 8u)");
        dispatch.ShouldContain("_radixHistogramProgram");
        dispatch.ShouldContain("_radixPrefixProgram");
        dispatch.ShouldContain("_radixScatterProgram");
        scatter.ShouldContain("for (uint prior = 0u; prior < lane; ++prior)");
        scatter.ShouldContain("localRank += uint(digits[prior] == digit)");
    }

    [Test]
    public void Refit_DispatchesInternalClearAndLeafWorkAtTheirLogicalCounts()
    {
        string dispatch = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Dispatch.cs");
        string shader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_refit.comp");

        dispatch.ShouldContain("ComputeGroups(internalCount, 256u)");
        dispatch.ShouldContain("ComputeGroups(leafCount, 256u)");
        shader.ShouldContain("uint counterIndex = parent - leafCount");
        shader.ShouldContain("if (leafIndex < leafCount)");
        shader.ShouldNotContain("uvec2 ranges[]");
    }

    [Test]
    public void FrustumTraversal_IsPartitionedRootDownPlaneMaskedAndConservativeOnQueuePressure()
    {
        string shader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_frustum_cull.comp");
        string dispatch = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.CullingAndSoA.cs");

        shader.ShouldContain("SelectPartitionRoot(safeNodeCount, partitionRoot)");
        shader.ShouldContain("uint partitionCount = gl_NumWorkGroups.x");
        shader.ShouldContain("(workgroup & suffixMask) == 0u");
        shader.ShouldContain("TraversalQueue[0] = uvec2(partitionRoot");
        shader.ShouldContain("outputMask &= ~bit");
        shader.ShouldContain("TryEnqueue(child, childPlaneMask)");
        shader.ShouldContain("ProcessRange(nodes[child].primitiveRange, childPlaneMask, false)");
        shader.ShouldNotContain("node.parentIndex");
        shader.ShouldNotContain("PrimitiveRanges");
        dispatch.ShouldContain("GpuBvhCullingDispatch.CalculateWorkgroupCount(inputCount)");
        dispatch.ShouldContain("DispatchCompute(traversalWorkgroups, 1u, 1u");
    }

    [Test]
    public void StaticCommandPublication_DoesNotInferAabbChangesFromSnapshotSwap()
    {
        string swap = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.CommandBuffers.cs");
        string bounds = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.BoundsHelpers.cs");

        swap.ShouldContain("bool commandSnapshotDirty");
        swap.ShouldContain("if (!commandSnapshotDirty)");
        swap.ShouldNotContain("_bvhRefitPending = true");
        bounds.ShouldContain("_commandAabbDirtyRange.Mark(commandIndex)");
        bounds.ShouldContain("FlushCommandAabbDirtyRange");
        bounds.ShouldContain("Interlocked.Increment(ref _commandAabbRevision)");
    }

    [Test]
    public void LateBvhActivation_BackfillsEveryCommandFromAuthoritativeBoundsSnapshot()
    {
        string scene = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.Bvh.cs");
        string bounds = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.BoundsHelpers.cs");

        scene.ShouldContain("_commandAabbBackfillRequired");
        scene.ShouldContain("BackfillCommandAabbsFromRenderSnapshot(commandCount)");
        int rebuildStart = scene.IndexOf("private bool RebuildInternalBvh()", StringComparison.Ordinal);
        int backfill = scene.IndexOf("BackfillCommandAabbsFromRenderSnapshot(commandCount)", rebuildStart, StringComparison.Ordinal);
        int flush = scene.IndexOf("FlushCommandAabbDirtyRange()", rebuildStart, StringComparison.Ordinal);
        backfill.ShouldBeGreaterThan(rebuildStart);
        flush.ShouldBeGreaterThan(backfill);
        bounds.ShouldContain("GetDataRawAtIndex<DrawMetadata>(commandIndex).BoundsID");
        bounds.ShouldContain("GetDataRawAtIndex<BoundsGpu>(boundsId)");
        bounds.ShouldContain("bounds.BoundsVersion != 0u");
        bounds.ShouldContain("forceDirty: true");
        bounds.ShouldContain("if (IsCommandOwnedByGpuAabb(commandIndex))");
        bounds.ShouldContain("command.BoundingSphere");
        bounds.ShouldContain("configuredBounds.IsValid");
    }

    [Test]
    public void CommandSwapRemoval_MovesTheDenseBvhAabbSlotWithTheCommand()
    {
        string removal = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.AddRemove.cs");
        string bounds = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.BoundsHelpers.cs");

        removal.ShouldContain("MoveCommandAabb(lastIndex, targetIndex);");
        bounds.ShouldContain("CommandWorldAabb moved = buffer.GetDataRawAtIndex<CommandWorldAabb>(sourceIndex);");
        bounds.ShouldContain("WriteCommandAabb(targetIndex, moved, uploadImmediately: false);");
    }

    [Test]
    public void WorldBoundsChanges_ArePublishedThroughVisualSceneSetBounds()
    {
        string world = ReadWorkspaceFile("XREngine/Rendering/XRWorldInstance.cs");

        world.ShouldContain("case nameof(WorldSettings.Bounds):");
        world.ShouldContain("VisualScene.SetBounds(settings.Bounds)");
    }

    [Test]
    public void MotionQualityPolicy_PeriodicallyRebuildsAndHasNormalizationHysteresis()
    {
        string scene = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.Bvh.cs");
        string bounds = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.BoundsHelpers.cs");

        scene.ShouldContain("MaxConsecutiveRefits = 120u");
        scene.ShouldContain("_consecutiveBvhRefits >= MaxConsecutiveRefits");
        bounds.ShouldContain("Retain a still-useful prior domain");
        bounds.ShouldContain("BvhConfiguredBoundsMaxAxisDilution = 2.0f");
        bounds.ShouldContain("liveSize.X * BvhConfiguredBoundsMaxAxisDilution");
        bounds.ShouldContain("BvhNormalizationHysteresisMaxVolumeRatio = 4.0f");
        bounds.ShouldContain("Volume(candidate) * BvhNormalizationHysteresisMaxVolumeRatio");
    }

    [Test]
    public void RayTraversal_OrdersNearChildAndPacketModeMasksInactiveLanes()
    {
        string core = ReadWorkspaceFile("Build/CommonAssets/Shaders/Snippets/BvhRaycastCore.glsl");
        string packet = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/BVH/bvh_raycast.comp");

        core.ShouldContain("Push far first so the near child is popped first");
        core.ShouldContain("TracePrimitiveRange(ray, node.primitiveRange.x");
        packet.ShouldContain("if (gl_LocalInvocationID.x >= uPacketWidth)");
    }

    [Test]
    public void QualityDiagnostics_AreGpuResidentOverflowSafeAndCadenced()
    {
        string shader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_quality_diagnostics.comp");
        string tree = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs");
        string dispatch = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Dispatch.cs");

        shader.ShouldContain("(code >> 24u) & 63u");
        shader.ShouldContain("values[LCP_BINS + min(lcp, 32u)]");
        shader.ShouldContain("sahProxy / sampleCount");
        shader.ShouldContain("overlapProxy / sampleCount");
        tree.ShouldContain("QualityAnalysisRefitCadence = 30u");
        tree.ShouldContain("% QualityAnalysisRefitCadence == 0u");
        dispatch.ShouldContain("DispatchQualityAnalysis");
    }

    [Test]
    public void TransferAndReadbackDiagnostics_ReportExactBytesAndZeroReadbackSkips()
    {
        string bounds = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.BoundsHelpers.cs");
        string overflow = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Overflow.cs");
        string diagnostics = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhDiagnostics.cs");

        bounds.ShouldContain("_pendingCommandAabbDirtyLeafCount++");
        bounds.ShouldContain("_pendingCommandAabbUploadBytes += bytes");
        overflow.ShouldContain("_zeroReadbackSubmissionCount++");
        overflow.ShouldContain("_synchronousReadbackBytes += sizeof(uint)");
        overflow.ShouldContain("_asynchronousReadbackBytes += sizeof(uint)");
        diagnostics.ShouldContain("ulong SynchronousReadbackBytes");
    }

    [Test]
    public void CommandEmission_HasTimingScopesIndependentFromTraversalDispatch()
    {
        string culling = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.CullingAndSoA.cs");
        string indirect = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");

        culling.ShouldContain("BvhGpuProfiler.Stage.Traversal");
        indirect.ShouldContain("BvhGpuProfiler.Stage.CommandEmission");
        indirect.ShouldContain("_indirectRenderTaskShader.DispatchCompute");
        indirect.ShouldContain("_materialScatterComputeShader.DispatchCompute");
        string packet = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");
        string panel = ReadWorkspaceFile("XREngine.Profiler.UI/ProfilerPanelRenderer.cs");
        packet.ShouldContain("CommandEmissionMilliseconds");
        panel.ShouldContain("Command emission:");
    }

    [Test]
    public void RayTraversal_ReportsStackOccupancyAndConservativeRecoveryOnGpu()
    {
        string core = ReadWorkspaceFile("Build/CommonAssets/Shaders/Snippets/BvhRaycastCore.glsl");
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/BvhRaycastDispatcher.cs");

        core.ShouldContain("atomicMax(gRayMaxStackOccupancy, stackPtr)");
        core.ShouldContain("atomicAdd(gRayStackOverflows, 1u)");
        core.ShouldContain("atomicAdd(gRayConservativeRecoveries, 1u)");
        dispatcher.ShouldContain("TraversalDiagnosticsBuffer");
        dispatcher.ShouldContain("uDiagnosticsEnabled");
    }

    [Test]
    public void RayTraversal_AlwaysBindsWritableDiagnosticsDescriptor()
    {
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/BvhRaycastDispatcher.cs");

        dispatcher.ShouldContain("request.TraversalDiagnosticsBuffer ?? EnsureFallbackTraversalDiagnosticsBuffer()");
        dispatcher.ShouldContain("program.BindBuffer(traversalDiagnostics, 4)");
        dispatcher.ShouldContain("_fallbackTraversalDiagnosticsBuffer?.Dispose()");
        dispatcher.ShouldNotContain("if (request.TraversalDiagnosticsBuffer is not null)\n            program.BindBuffer(request.TraversalDiagnosticsBuffer, 4)");
    }

    [Test]
    public void RepresentativeWorldCenters_OccupyDistinctMortonRanges()
    {
        Vector3 min = new(-100.0f);
        Vector3 max = new(100.0f);
        Vector3[] centers =
        [
            new(-90.0f, -90.0f, -90.0f),
            new(-25.0f, 20.0f, 5.0f),
            new(0.0f, 0.0f, 0.0f),
            new(35.0f, -40.0f, 70.0f),
            new(90.0f, 90.0f, 90.0f),
        ];

        centers.Select(center => Morton3D(Vector3.Clamp((center - min) / (max - min), Vector3.Zero, Vector3.One)))
            .Distinct()
            .Count()
            .ShouldBe(centers.Length);
    }

    private static uint Morton3D(Vector3 normalized)
    {
        uint x = ExpandBits((uint)(normalized.X * 1023.0f));
        uint y = ExpandBits((uint)(normalized.Y * 1023.0f));
        uint z = ExpandBits((uint)(normalized.Z * 1023.0f));
        return x | (y << 1) | (z << 2);
    }

    private static uint ExpandBits(uint value)
    {
        value = (value * 0x00010001u) & 0xFF0000FFu;
        value = (value * 0x00000101u) & 0x0F00F00Fu;
        value = (value * 0x00000011u) & 0xC30C30C3u;
        value = (value * 0x00000005u) & 0x49249249u;
        return value;
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = Path.Combine(ResolveWorkspaceRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(fullPath).ShouldBeTrue($"Expected workspace file to exist: {relativePath}");
        return File.ReadAllText(fullPath).Replace("\r\n", "\n");
    }

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find workspace root from '{AppContext.BaseDirectory}'.");
    }
}
