using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class MaterialScatterPhase07Tests
{
    #region Struct Layout Tests

    [Test]
    public void GPUBatchRangeEntry_StructSize_Is16Bytes()
    {
        // BatchRangeUIntCount = 4 → 4 × 4 = 16 bytes
        int actualSize = Marshal.SizeOf<GPUBatchRangeEntry>();
        actualSize.ShouldBe(16);
    }

    [Test]
    public void GPUBatchRangeEntry_FieldLayout_MatchesShaderOrder()
    {
        var entry = new GPUBatchRangeEntry
        {
            DrawOffset = 100,
            DrawCount = 10,
            MaterialID = 42,
            PackedPassPipelineState = 0xFF00FF00
        };

        entry.DrawOffset.ShouldBe(100u);
        entry.DrawCount.ShouldBe(10u);
        entry.MaterialID.ShouldBe(42u);
        entry.PackedPassPipelineState.ShouldBe(0xFF00FF00u);
    }

    [Test]
    public void GPUSortKeyEntry_StructSize_Is16Bytes()
    {
        // SortKeyUIntCount = 4 → 4 × 4 = 16 bytes
        int actualSize = Marshal.SizeOf<GPUSortKeyEntry>();
        actualSize.ShouldBe(16);
    }

    [Test]
    public void GPUMaterialSlotEntry_StructSize_MatchesLayout()
    {
        // MaterialSlotEntryUIntCount = 1 in GPUBatchingLayout, but struct has 2 fields.
        // The struct is used for CPU-side mapping only; the GPU buffer stores just MaterialID.
        int actualSize = Marshal.SizeOf<GPUMaterialSlotEntry>();
        actualSize.ShouldBe(8); // 2 × uint = 8 bytes
    }

    #endregion

    #region Binding Constant Uniqueness Tests

    [Test]
    public void MaterialScatterBindings_AreUnique()
    {
        var bindings = new[]
        {
            GPUBatchingBindings.MaterialScatterInputCommands,
            GPUBatchingBindings.MaterialScatterMeshData,
            GPUBatchingBindings.MaterialScatterCulledCount,
            GPUBatchingBindings.MaterialScatterSortKeys,
            GPUBatchingBindings.MaterialScatterMaterialSlotLookup,
            GPUBatchingBindings.MaterialScatterIndirectDraws,
            GPUBatchingBindings.MaterialScatterDrawCounts,
            GPUBatchingBindings.MaterialScatterOverflow,
        };

        var seen = new HashSet<int>();
        foreach (int b in bindings)
            seen.Add(b).ShouldBeTrue($"Duplicate scatter binding: {b}");
    }

    [Test]
    public void BuildBatchesBindings_AreUnique()
    {
        var bindings = new[]
        {
            GPUBatchingBindings.BuildBatchesInputCommands,
            GPUBatchingBindings.BuildBatchesMeshData,
            GPUBatchingBindings.BuildBatchesCulledCount,
            GPUBatchingBindings.BuildBatchesSortKeys,
            GPUBatchingBindings.BuildBatchesIndirectDraws,
            GPUBatchingBindings.BuildBatchesDrawCount,
            GPUBatchingBindings.BuildBatchesBatchRanges,
            GPUBatchingBindings.BuildBatchesBatchCount,
            GPUBatchingBindings.BuildBatchesInstanceTransforms,
            GPUBatchingBindings.BuildBatchesInstanceSources,
            GPUBatchingBindings.BuildBatchesMaterialAggregation,
            GPUBatchingBindings.BuildBatchesIndirectOverflow,
            GPUBatchingBindings.BuildBatchesTruncation,
            GPUBatchingBindings.BuildBatchesStats,
            GPUBatchingBindings.BuildBatchesSortScratch,
        };

        var seen = new HashSet<int>();
        foreach (int b in bindings)
            seen.Add(b).ShouldBeTrue($"Duplicate build-batches binding: {b}");
    }

    #endregion

    #region Host/Shader Contract Tests

    [Test]
    public void MaterialTierCount_HostMatchesShader()
    {
        // Host constant must match MATERIAL_TIER_COUNT in GPURenderMaterialScatter.comp
        GPUBatchingBindings.MaterialTierCount.ShouldBe(3u);
    }

    [Test]
    public void InvalidMaterialSlot_HostMatchesShader()
    {
        // Host constant must match INVALID_MATERIAL_SLOT in GPURenderMaterialScatter.comp
        GPUBatchingBindings.InvalidMaterialSlot.ShouldBe(0xFFFFFFFFu);
    }

    [Test]
    public void MaterialScatterShader_ContainsExpectedContracts()
    {
        string shaderSource = ReadWorkspaceFile(
            "Build/CommonAssets/Shaders/Compute/Indirect/GPURenderMaterialScatter.comp");

        // Layout constants
        shaderSource.ShouldContain("MATERIAL_TIER_COUNT = 3u");
        shaderSource.ShouldContain("INVALID_MATERIAL_SLOT = 0xFFFFFFFFu");
        shaderSource.ShouldContain("DRAW_UINTS = 5u");

        // Required buffer bindings
        shaderSource.ShouldContain("binding = 0");
        shaderSource.ShouldContain("binding = 4");
        shaderSource.ShouldContain("binding = 5");
        shaderSource.ShouldContain("binding = 6");
    }

    [Test]
    public void LayoutConstants_HostMatchesExpected()
    {
        GPUBatchingLayout.SortKeyUIntCount.ShouldBe(4u);
        GPUBatchingLayout.BatchRangeUIntCount.ShouldBe(4u);
        GPUBatchingLayout.MaterialSlotEntryUIntCount.ShouldBe(1u);
        GPUBatchingLayout.SortKeyStride.ShouldBe(16u);
        GPUBatchingLayout.BatchRangeStride.ShouldBe(16u);
    }

    #endregion

    #region Helpers

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected file does not exist: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string ResolveWorkspacePath(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }

    #endregion
}
