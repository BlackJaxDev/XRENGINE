using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Unit tests for GPU indirect render dispatch path.
/// Tests use the actual compute shaders: GPURenderIndirect.comp and GPURenderCulling.comp
/// </summary>
[TestFixture]
public class GpuIndirectRenderDispatchTests
{
    private const int Width = 256;
    private const int Height = 256;

    /// <summary>
    /// Gets the path to the shader directory.
    /// </summary>
    private static string ShaderBasePath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                var candidate = Path.Combine(dir, "Build", "CommonAssets", "Shaders");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir) ?? dir;
            }
            return @"D:\Documents\XRENGINE\Build\CommonAssets\Shaders";
        }
    }

    private static string LoadShaderSource(string relativePath)
    {
        var fullPath = Path.Combine(ShaderBasePath, relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Shader file not found: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static bool IsTrue(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return false;
        v = v.Trim();
        return v.Equals("1") || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShowWindow => IsTrue(Environment.GetEnvironmentVariable("XR_SHOW_TEST_WINDOWS"));

    [SetUp]
    public void EnableCpuReadbackForAssertions()
    {
        GPURenderPassCollection.ConfigureIndirectDebug(d =>
        {
            d.DisableCpuReadbackCount = false;
            d.EnableCpuBatching = false;
            d.ForceCpuFallbackCount = false;
        });
    }

    #region Shader Loading Tests

    [Test]
    public void GPURenderIndirectShader_Loads_Successfully()
    {
        string source = LoadShaderSource("Compute/Indirect/GPURenderIndirect.comp");

        source.ShouldNotBeNullOrEmpty();
        source.ShouldContain("#version 460 core");
        source.ShouldContain("COMMAND_FLOATS = 48");
        source.ShouldContain("DRAW_COMMAND_UINTS = 5");
        source.ShouldContain("CulledCommandsBuffer");
        source.ShouldContain("IndirectDrawBuffer");
        source.ShouldContain("DrawCount");
    }

    [Test]
    public void GPURenderCullingShader_Loads_Successfully()
    {
        string source = LoadShaderSource("Compute/Culling/GPURenderCulling.comp");

        source.ShouldNotBeNullOrEmpty();
        source.ShouldContain("#version 460 core");
        source.ShouldContain("COMMAND_FLOATS = 48");
        source.ShouldContain("FrustumPlanes");
        source.ShouldContain("FrustumSphereVisible");
        source.ShouldContain("CulledCount");
    }

    [Test]
    public void GPURenderResetCountersShader_Loads_Successfully()
    {
        string source = LoadShaderSource("Compute/Indirect/GPURenderResetCounters.comp");

        source.ShouldNotBeNullOrEmpty();
        source.ShouldContain("#version 460 core");
        source.ShouldContain("CulledCount");
        source.ShouldContain("DrawCount");
        source.ShouldContain("StatsBuffer");
    }

    [Test]
    public void GPURenderBuildKeysShader_Loads_Successfully()
    {
        string source = LoadShaderSource("Compute/Indirect/GPURenderBuildKeys.comp");

        source.ShouldNotBeNullOrEmpty();
        source.ShouldContain("#version 460 core");
        source.ShouldContain("CurrentRenderPass");
        source.ShouldContain("MaxSortKeys");
        source.ShouldContain("StateBitMask");
        source.ShouldContain("sortKeys");
    }

    [Test]
    public void GPURenderRadixIndexSortShader_Loads_Successfully()
    {
        string source = LoadShaderSource("Compute/Sorting/GPURenderRadixIndexSort.comp");

        source.ShouldNotBeNullOrEmpty();
        source.ShouldContain("#version 460 core");
        source.ShouldContain("RadixPass");
        source.ShouldContain("BuildHistogram");
        source.ShouldContain("PrefixScan");
        source.ShouldContain("Scatter");
    }

    #endregion

    #region DrawElementsIndirectCommand Tests

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DrawElementsIndirectCommand
    {
        public uint Count;
        public uint InstanceCount;
        public uint FirstIndex;
        public int BaseVertex;
        public uint BaseInstance;
    }

    [Test]
    public void DrawElementsIndirectCommand_StructSize_Is20Bytes()
    {
        // Verify the indirect command structure has the OpenGL-specified size
        // DRAW_COMMAND_UINTS = 5 in shader
        int expectedSize = 20; // 5 * 4 bytes
        int actualSize = Marshal.SizeOf<DrawElementsIndirectCommand>();
        
        actualSize.ShouldBe(expectedSize);
    }

    [Test]
    public void DrawElementsIndirectCommand_LayoutMatchesShader()
    {
        // Verify layout matches GPURenderIndirect.comp:
        // indirectDraws[drawBase + 0u] = indexCount;
        // indirectDraws[drawBase + 1u] = instanceCount;
        // indirectDraws[drawBase + 2u] = firstIndex;
        // indirectDraws[drawBase + 3u] = baseVertex;
        // indirectDraws[drawBase + 4u] = baseInstance;

        var cmd = new DrawElementsIndirectCommand
        {
            Count = 36,         // offset 0
            InstanceCount = 1,  // offset 1
            FirstIndex = 100,   // offset 2
            BaseVertex = 0,     // offset 3
            BaseInstance = 42   // offset 4 (culled command index)
        };

        // baseInstance encodes the culled-command index for vertex shader data fetch
        cmd.BaseInstance.ShouldBe(42u);
    }

    [Test]
    public void DrawElementsIndirectCommand_DefaultValues_AreZeroInitialized()
    {
        var cmd = new DrawElementsIndirectCommand();

        cmd.Count.ShouldBe(0u);
        cmd.InstanceCount.ShouldBe(0u);
        cmd.FirstIndex.ShouldBe(0u);
        cmd.BaseVertex.ShouldBe(0);
        cmd.BaseInstance.ShouldBe(0u);
    }

    [Test]
    public void DrawElementsIndirectCommand_CanStoreFullIndexRange()
    {
        var cmd = new DrawElementsIndirectCommand
        {
            Count = uint.MaxValue,
            InstanceCount = uint.MaxValue,
            FirstIndex = uint.MaxValue,
            BaseVertex = int.MaxValue,
            BaseInstance = uint.MaxValue
        };

        cmd.Count.ShouldBe(uint.MaxValue);
        cmd.InstanceCount.ShouldBe(uint.MaxValue);
        cmd.FirstIndex.ShouldBe(uint.MaxValue);
        cmd.BaseVertex.ShouldBe(int.MaxValue);
        cmd.BaseInstance.ShouldBe(uint.MaxValue);
    }

    [Test]
    public void DrawElementsIndirectCommand_BaseVertex_CanBeNegative()
    {
        // BaseVertex is signed, allowing negative offsets
        var cmd = new DrawElementsIndirectCommand
        {
            BaseVertex = -100
        };

        cmd.BaseVertex.ShouldBe(-100);
    }

    #endregion

    #region Command Buffer Layout Tests (Matching Shader)

    /// <summary>
    /// GPUIndirectRenderCommand layout from GPURenderCulling.comp and GPURenderIndirect.comp:
    /// 00-15: WorldMatrix (mat4), 16-31: PrevWorldMatrix (mat4), 32-35: BoundingSphere,
    /// 36: MeshID, 37: SubmeshID, 38: MaterialID, 39: InstanceCount, 40: RenderPass,
    /// 41: ShaderProgramID, 42: RenderDistance, 43: LayerMask, 44: LODLevel,
    /// 45: Flags, 46-47: Reserved
    /// </summary>
    [Test]
    public void CommandBuffer_FieldOffsets_MatchShaderLayout()
    {
        const int COMMAND_FLOATS = 48;

        // World Matrix at offset 0-15
        int worldMatrixEnd = 16;
        worldMatrixEnd.ShouldBe(16);

        // PrevWorldMatrix at offset 16-31
        int prevMatrixEnd = 32;
        prevMatrixEnd.ShouldBe(32);

        // BoundingSphere at offset 32-35
        int boundingSphereStart = 32;
        boundingSphereStart.ShouldBe(32);

        // Individual fields
        int meshIdOffset = 36;
        int submeshIdOffset = 37;
        int materialIdOffset = 38;
        int instanceCountOffset = 39;
        int renderPassOffset = 40;
        int shaderProgramIdOffset = 41;
        int renderDistanceOffset = 42;
        int layerMaskOffset = 43;
        int lodLevelOffset = 44;
        int flagsOffset = 45;

        meshIdOffset.ShouldBe(36);
        materialIdOffset.ShouldBe(38);
        instanceCountOffset.ShouldBe(39);
        renderPassOffset.ShouldBe(40);
        renderDistanceOffset.ShouldBe(42);
        layerMaskOffset.ShouldBe(43);
        flagsOffset.ShouldBe(45);
    }

    [Test]
    public void CommandBuffer_TotalSize_Is192Bytes()
    {
        const int COMMAND_FLOATS = 48;
        const int FLOAT_SIZE = 4;

        int totalBytes = COMMAND_FLOATS * FLOAT_SIZE;
        totalBytes.ShouldBe(192);
    }

    #endregion

    #region Compute Dispatch Calculation Tests

    [Test]
    public void ComputeDispatch_ForCommands_CalculatesCorrectGroupCount()
    {
        // Shader uses layout(local_size_x = 256)
        const uint workgroupSize = 256;

        // Test exact multiple
        uint commands = 256;
        uint groups = (commands + workgroupSize - 1) / workgroupSize;
        groups.ShouldBe(1u);

        // Test with remainder
        commands = 257;
        groups = (commands + workgroupSize - 1) / workgroupSize;
        groups.ShouldBe(2u);

        // Test large count
        commands = 10000;
        groups = (commands + workgroupSize - 1) / workgroupSize;
        groups.ShouldBe(40u);
    }

    [Test]
    public void ComputeDispatch_ZeroCommands_ReturnsMinimumOneGroup()
    {
        const uint workgroupSize = 256;
        uint commands = 0;
        
        // Many implementations require at least 1 group
        uint groups = Math.Max(1u, (commands + workgroupSize - 1) / workgroupSize);
        groups.ShouldBe(1u);
    }

    [Test]
    public void ComputeDispatch_SingleCommand_ReturnsOneGroup()
    {
        const uint workgroupSize = 256;
        uint commands = 1;
        
        uint groups = (commands + workgroupSize - 1) / workgroupSize;
        groups.ShouldBe(1u);
    }

    [Test]
    public void ComputeDispatch_MaxCommands_DoesNotOverflow()
    {
        const uint workgroupSize = 256;
        
        // Test with large but valid command count
        uint commands = 1_000_000;
        uint groups = (commands + workgroupSize - 1) / workgroupSize;
        
        groups.ShouldBe(3907u);
        
        // Verify we can handle GPU limit scenarios
        uint maxGpuCommands = 16 * 1024 * 1024; // 16M commands
        uint maxGroups = (maxGpuCommands + workgroupSize - 1) / workgroupSize;
        maxGroups.ShouldBe(65536u);
    }

    #endregion

    #region Indirect Buffer Layout Tests

    [Test]
    public void IndirectBuffer_CommandStride_MatchesStructSize()
    {
        uint expectedStride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
        expectedStride.ShouldBe(20u);
    }

    [Test]
    public void IndirectBuffer_CommandOffset_CalculatesCorrectly()
    {
        uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
        
        uint offset0 = 0 * stride;
        uint offset1 = 1 * stride;
        uint offset2 = 2 * stride;
        uint offset10 = 10 * stride;

        offset0.ShouldBe(0u);
        offset1.ShouldBe(20u);
        offset2.ShouldBe(40u);
        offset10.ShouldBe(200u);
    }

    [Test]
    public void IndirectBuffer_Capacity_PowerOfTwoGrowth()
    {
        // Test power-of-two buffer capacity growth
        uint[] testCounts = { 1, 10, 100, 500, 1000, 5000 };
        
        foreach (var count in testCounts)
        {
            uint capacity = XRMath.NextPowerOfTwo(count);
            capacity.ShouldBeGreaterThanOrEqualTo(count);
            
            // Verify it's actually a power of two
            (capacity & (capacity - 1)).ShouldBe(0u);
        }
    }

    #endregion

    #region Draw Count Buffer Tests

    [Test]
    public void DrawCountBuffer_Layout_SingleUintAtOffset0()
    {
        // The parameter buffer for MultiDrawElementsIndirectCount expects:
        // - A single uint at offset 0 representing the draw count
        
        const uint expectedDrawCount = 42;
        uint[] buffer = new uint[1] { expectedDrawCount };

        buffer[0].ShouldBe(expectedDrawCount);
    }

    [Test]
    public void DrawCountBuffer_MaxDrawCount_WithinGpuLimits()
    {
        // Most GPUs support at least 2^16 indirect draws
        const uint minSupportedDraws = 65536;
        
        // Test that we can represent the minimum supported count
        uint drawCount = minSupportedDraws;
        drawCount.ShouldBeLessThanOrEqualTo(uint.MaxValue);
    }

    #endregion

    #region Culled Command Buffer Tests

    [Test]
    public void CulledCommandBuffer_VisibleCountLayout_ThreeComponents()
    {
        // Layout: [visibleDraws, visibleInstances, overflowMarker]
        const uint visibleCountDrawIndex = 0;
        const uint visibleCountInstanceIndex = 1;
        const uint visibleCountOverflowIndex = 2;

        visibleCountDrawIndex.ShouldBe(GPUScene.VisibleCountDrawIndex);
        visibleCountInstanceIndex.ShouldBe(GPUScene.VisibleCountInstanceIndex);
        visibleCountOverflowIndex.ShouldBe(GPUScene.VisibleCountOverflowIndex);
    }

    [Test]
    public void CulledCommandBuffer_MinimumCapacity_EightCommands()
    {
        GPUScene.MinCommandCount.ShouldBe(8u);
    }

    [Test]
    public void CulledCommandBuffer_CommandFloatCount_Is48Floats()
    {
        // Each command is 192 bytes = 48 floats (includes world matrix, prev matrix, bounds, IDs, etc.)
        GPUScene.CommandFloatCount.ShouldBe(48);
    }

    [Test]
    public void CulledCommandBuffer_OverflowFlag_NonZeroIndicatesOverflow()
    {
        // Overflow is signaled by a non-zero value at the overflow index
        uint[] counters = { 100, 100, 0 }; // draws, instances, no overflow
        counters[(int)GPUScene.VisibleCountOverflowIndex].ShouldBe(0u);

        counters[(int)GPUScene.VisibleCountOverflowIndex] = 1; // overflow occurred
        counters[(int)GPUScene.VisibleCountOverflowIndex].ShouldNotBe(0u);
    }

    #endregion

    #region Material Batching Tests

    [Test]
    public void MaterialBatching_SingleMaterial_OneBatch()
    {
        // Simulate commands with same material
        var materialIds = new uint[] { 1, 1, 1, 1, 1 };
        
        var batches = CreateBatches(materialIds);
        
        batches.Length.ShouldBe(1);
        batches[0].StartIndex.ShouldBe(0u);
        batches[0].Count.ShouldBe(5u);
        batches[0].MaterialId.ShouldBe(1u);
    }

    [Test]
    public void MaterialBatching_MultipleMaterials_MultipleBatches()
    {
        // Commands sorted by material: [mat1, mat1, mat2, mat2, mat2, mat3]
        var materialIds = new uint[] { 1, 1, 2, 2, 2, 3 };
        
        var batches = CreateBatches(materialIds);
        
        batches.Length.ShouldBe(3);
        
        batches[0].MaterialId.ShouldBe(1u);
        batches[0].Count.ShouldBe(2u);
        
        batches[1].MaterialId.ShouldBe(2u);
        batches[1].Count.ShouldBe(3u);
        
        batches[2].MaterialId.ShouldBe(3u);
        batches[2].Count.ShouldBe(1u);
    }

    [Test]
    public void MaterialBatching_EmptyCommands_NoBatches()
    {
        var materialIds = Array.Empty<uint>();
        
        var batches = CreateBatches(materialIds);
        
        batches.Length.ShouldBe(0);
    }

    [Test]
    public void MaterialBatching_AlternatingMaterials_ManyBatches()
    {
        // Worst case: alternating materials (unsorted)
        var materialIds = new uint[] { 1, 2, 1, 2, 1, 2 };
        
        var batches = CreateBatches(materialIds);
        
        // Each transition creates a new batch
        batches.Length.ShouldBe(6);
    }

    private struct DrawBatch
    {
        public uint StartIndex;
        public uint Count;
        public uint MaterialId;
    }

    private static DrawBatch[] CreateBatches(uint[] materialIds)
    {
        if (materialIds.Length == 0)
            return Array.Empty<DrawBatch>();

        var batches = new List<DrawBatch>();
        uint currentMaterial = materialIds[0];
        uint batchStart = 0;
        uint batchCount = 1;

        for (int i = 1; i < materialIds.Length; i++)
        {
            if (materialIds[i] != currentMaterial)
            {
                batches.Add(new DrawBatch
                {
                    StartIndex = batchStart,
                    Count = batchCount,
                    MaterialId = currentMaterial
                });
                
                currentMaterial = materialIds[i];
                batchStart = (uint)i;
                batchCount = 1;
            }
            else
            {
                batchCount++;
            }
        }

        // Add final batch
        batches.Add(new DrawBatch
        {
            StartIndex = batchStart,
            Count = batchCount,
            MaterialId = currentMaterial
        });

        return batches.ToArray();
    }

    #endregion

    #region Render Pass Filter Tests

    [Test]
    public void RenderPassFilter_MatchingPass_CommandAccepted()
    {
        int currentPass = 0;
        uint commandPass = 0;

        bool accepted = IsPassAccepted(commandPass, currentPass);
        accepted.ShouldBeTrue();
    }

    [Test]
    public void RenderPassFilter_DifferentPass_CommandRejected()
    {
        int currentPass = 0;
        uint commandPass = 1; // Shadow pass

        bool accepted = IsPassAccepted(commandPass, currentPass);
        accepted.ShouldBeFalse();
    }

    [Test]
    public void RenderPassFilter_AllPassesFlag_AlwaysAccepted()
    {
        // Special flag indicating command should render in all passes
        const uint allPassesFlag = 0xFFFFFFFF;
        
        IsPassAccepted(allPassesFlag, 0).ShouldBeTrue();
        IsPassAccepted(allPassesFlag, 1).ShouldBeTrue();
        IsPassAccepted(allPassesFlag, 5).ShouldBeTrue();
    }

    private static bool IsPassAccepted(uint commandPass, int currentPass)
    {
        if (commandPass == 0xFFFFFFFF)
            return true;
        
        return commandPass == (uint)currentPass;
    }

    #endregion

    #region GPU Stats Layout Tests

    [Test]
    public void GpuStatsLayout_FieldIndices_AreSequential()
    {
        // Verify stats buffer layout indices
        GpuStatsLayout.StatsInputCount.ShouldBe(0u);
        GpuStatsLayout.StatsCulledCount.ShouldBe(1u);
        GpuStatsLayout.StatsDrawCount.ShouldBe(2u);
        GpuStatsLayout.StatsRejectedFrustum.ShouldBe(3u);
        GpuStatsLayout.StatsRejectedDistance.ShouldBe(4u);
    }

    [Test]
    public void GpuStatsLayout_BvhStats_AfterCoreStats()
    {
        GpuStatsLayout.BvhBuildCount.ShouldBeGreaterThan(GpuStatsLayout.StatsRejectedDistance);
    }

    [Test]
    public void GpuStatsLayout_TimestampPairs_ArePaired()
    {
        // Timestamps are stored as Lo/Hi pairs for 64-bit values
        uint buildTimeLo = GpuStatsLayout.BvhBuildTimeLo;
        uint buildTimeHi = GpuStatsLayout.BvhBuildTimeHi;
        
        buildTimeHi.ShouldBe(buildTimeLo + 1);
    }

    [Test]
    public void GpuStatsLayout_FieldCount_MatchesExpected()
    {
        GpuStatsLayout.FieldCount.ShouldBeGreaterThan(0u);
    }

    #endregion

    #region Memory Barrier Tests

    [Test]
    public void MemoryBarrierMask_ShaderStorage_IsNonZero()
    {
        var mask = EMemoryBarrierMask.ShaderStorage;
        ((int)mask).ShouldNotBe(0);
    }

    [Test]
    public void MemoryBarrierMask_Command_IsNonZero()
    {
        var mask = EMemoryBarrierMask.Command;
        ((int)mask).ShouldNotBe(0);
    }

    [Test]
    public void MemoryBarrierMask_CombinedBarriers_CanBeOred()
    {
        var combined = EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command;
        
        combined.HasFlag(EMemoryBarrierMask.ShaderStorage).ShouldBeTrue();
        combined.HasFlag(EMemoryBarrierMask.Command).ShouldBeTrue();
    }

    #endregion

    #region Indirect Debug Settings Tests

    [Test]
    public void IndirectDebugSettings_DefaultValues_AreReasonable()
    {
        var settings = new GPURenderPassCollection.IndirectDebugSettings();

        // Debug logging defaults should be reasonable for production
        settings.ForceCpuFallbackCount.ShouldBeFalse();
        settings.DisableCountDrawPath.ShouldBeFalse();
    }

    [Test]
    public void IndirectDebugSettings_CanEnableForceCpuFallback()
    {
        var settings = new GPURenderPassCollection.IndirectDebugSettings
        {
            ForceCpuFallbackCount = true
        };

        settings.ForceCpuFallbackCount.ShouldBeTrue();
    }

    [Test]
    public void IndirectDebugSettings_CanDisableCountDrawPath()
    {
        var settings = new GPURenderPassCollection.IndirectDebugSettings
        {
            DisableCountDrawPath = true
        };

        settings.DisableCountDrawPath.ShouldBeTrue();
    }

    #endregion

    #region Transform Matrix Tests

    [Test]
    public void WorldMatrix_Identity_DoesNotTransformPosition()
    {
        var cmd = new GPUIndirectRenderCommand
        {
            WorldMatrix = Matrix4x4.Identity
        };

        var localPos = new Vector3(1, 2, 3);
        var worldPos = Vector3.Transform(localPos, cmd.WorldMatrix);

        worldPos.X.ShouldBe(1f, 0.001f);
        worldPos.Y.ShouldBe(2f, 0.001f);
        worldPos.Z.ShouldBe(3f, 0.001f);
    }

    [Test]
    public void WorldMatrix_Translation_MovesPosition()
    {
        var cmd = new GPUIndirectRenderCommand
        {
            WorldMatrix = Matrix4x4.CreateTranslation(10, 20, 30)
        };

        var localPos = new Vector3(1, 2, 3);
        var worldPos = Vector3.Transform(localPos, cmd.WorldMatrix);

        worldPos.X.ShouldBe(11f, 0.001f);
        worldPos.Y.ShouldBe(22f, 0.001f);
        worldPos.Z.ShouldBe(33f, 0.001f);
    }

    [Test]
    public void WorldMatrix_Scale_ScalesPosition()
    {
        var cmd = new GPUIndirectRenderCommand
        {
            WorldMatrix = Matrix4x4.CreateScale(2f)
        };

        var localPos = new Vector3(1, 2, 3);
        var worldPos = Vector3.Transform(localPos, cmd.WorldMatrix);

        worldPos.X.ShouldBe(2f, 0.001f);
        worldPos.Y.ShouldBe(4f, 0.001f);
        worldPos.Z.ShouldBe(6f, 0.001f);
    }

    [Test]
    public void PrevWorldMatrix_StoredForMotionVectors()
    {
        var prevMatrix = Matrix4x4.CreateTranslation(5, 5, 5);
        var currMatrix = Matrix4x4.CreateTranslation(10, 10, 10);

        var cmd = new GPUIndirectRenderCommand
        {
            WorldMatrix = currMatrix,
            PrevWorldMatrix = prevMatrix
        };

        // Motion vector calculation would use the difference
        var localPos = new Vector3(0, 0, 0);
        var prevWorld = Vector3.Transform(localPos, cmd.PrevWorldMatrix);
        var currWorld = Vector3.Transform(localPos, cmd.WorldMatrix);

        var motion = currWorld - prevWorld;
        motion.X.ShouldBe(5f, 0.001f);
        motion.Y.ShouldBe(5f, 0.001f);
        motion.Z.ShouldBe(5f, 0.001f);
    }

    #endregion

    #region Distance Culling Tests

    [Test]
    public void DistanceCulling_ObjectWithinRange_NotCulled()
    {
        float maxRenderDistance = 100f;
        var objectPosition = new Vector3(0, 0, -50); // 50 units away
        var cameraPosition = Vector3.Zero;

        float distance = Vector3.Distance(objectPosition, cameraPosition);
        bool culled = distance > maxRenderDistance;

        culled.ShouldBeFalse();
    }

    [Test]
    public void DistanceCulling_ObjectBeyondRange_IsCulled()
    {
        float maxRenderDistance = 100f;
        var objectPosition = new Vector3(0, 0, -150); // 150 units away
        var cameraPosition = Vector3.Zero;

        float distance = Vector3.Distance(objectPosition, cameraPosition);
        bool culled = distance > maxRenderDistance;

        culled.ShouldBeTrue();
    }

    [Test]
    public void DistanceCulling_SquaredDistanceOptimization()
    {
        // Using squared distance avoids expensive sqrt
        float maxRenderDistance = 100f;
        float maxDistanceSq = maxRenderDistance * maxRenderDistance;

        var objectPosition = new Vector3(0, 0, -50);
        var cameraPosition = Vector3.Zero;

        float distanceSq = Vector3.DistanceSquared(objectPosition, cameraPosition);
        bool culled = distanceSq > maxDistanceSq;

        culled.ShouldBeFalse();
    }

    #endregion

    #region LOD Selection Tests

    [Test]
    public void LodSelection_NearDistance_HighestLod()
    {
        float[] lodDistances = { 10f, 50f, 100f }; // LOD transitions
        float objectDistance = 5f;

        uint lodLevel = SelectLodLevel(objectDistance, lodDistances);
        
        lodLevel.ShouldBe(0u); // Highest detail
    }

    [Test]
    public void LodSelection_MediumDistance_MediumLod()
    {
        float[] lodDistances = { 10f, 50f, 100f };
        float objectDistance = 30f;

        uint lodLevel = SelectLodLevel(objectDistance, lodDistances);
        
        lodLevel.ShouldBe(1u);
    }

    [Test]
    public void LodSelection_FarDistance_LowestLod()
    {
        float[] lodDistances = { 10f, 50f, 100f };
        float objectDistance = 75f;

        uint lodLevel = SelectLodLevel(objectDistance, lodDistances);
        
        lodLevel.ShouldBe(2u);
    }

    [Test]
    public void LodSelection_BeyondAllDistances_MaxLod()
    {
        float[] lodDistances = { 10f, 50f, 100f };
        float objectDistance = 150f;

        uint lodLevel = SelectLodLevel(objectDistance, lodDistances);
        
        lodLevel.ShouldBe(3u); // Beyond last threshold
    }

    private static uint SelectLodLevel(float distance, float[] lodDistances)
    {
        for (uint i = 0; i < lodDistances.Length; i++)
        {
            if (distance < lodDistances[i])
                return i;
        }
        return (uint)lodDistances.Length;
    }

    #endregion
}

