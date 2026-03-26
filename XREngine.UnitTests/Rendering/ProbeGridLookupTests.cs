using System.Collections.Generic;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Vectors;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ProbeGridLookupTests
{
    // ─── ComputeProbeGridFallbackIndices logic tests ───

    [Test]
    public void FallbackIndices_NoProbes_ReturnsAllNegativeOne()
    {
        var positions = new List<DefaultRenderPipeline.ProbePositionData>();
        var result = DefaultRenderPipeline.ComputeProbeGridFallbackIndices(Vector3.Zero, positions, null);

        result.X.ShouldBe(-1);
        result.Y.ShouldBe(-1);
        result.Z.ShouldBe(-1);
        result.W.ShouldBe(-1);
    }

    [Test]
    public void FallbackIndices_SingleProbe_ReturnsItInSlotZero()
    {
        var positions = new List<DefaultRenderPipeline.ProbePositionData>
        {
            new() { Position = new Vector4(5, 0, 0, 1) },
        };
        var result = DefaultRenderPipeline.ComputeProbeGridFallbackIndices(Vector3.Zero, positions, null);

        result.X.ShouldBe(0);
        result.Y.ShouldBe(-1);
        result.Z.ShouldBe(-1);
        result.W.ShouldBe(-1);
    }

    [Test]
    public void FallbackIndices_FourProbes_ReturnsSortedByDistance()
    {
        var positions = new List<DefaultRenderPipeline.ProbePositionData>
        {
            new() { Position = new Vector4(10, 0, 0, 1) }, // index 0, dist=10
            new() { Position = new Vector4(1, 0, 0, 1) },  // index 1, dist=1  (closest)
            new() { Position = new Vector4(5, 0, 0, 1) },  // index 2, dist=5
            new() { Position = new Vector4(3, 0, 0, 1) },  // index 3, dist=3
        };
        var result = DefaultRenderPipeline.ComputeProbeGridFallbackIndices(Vector3.Zero, positions, null);

        result.X.ShouldBe(1); // dist 1
        result.Y.ShouldBe(3); // dist 3
        result.Z.ShouldBe(2); // dist 5
        result.W.ShouldBe(0); // dist 10
    }

    [Test]
    public void FallbackIndices_MoreThanFourProbes_PicksFourNearest()
    {
        var positions = new List<DefaultRenderPipeline.ProbePositionData>
        {
            new() { Position = new Vector4(100, 0, 0, 1) }, // far
            new() { Position = new Vector4(2, 0, 0, 1) },   // near
            new() { Position = new Vector4(200, 0, 0, 1) }, // far
            new() { Position = new Vector4(3, 0, 0, 1) },   // near
            new() { Position = new Vector4(1, 0, 0, 1) },   // nearest
            new() { Position = new Vector4(4, 0, 0, 1) },   // near
        };
        var result = DefaultRenderPipeline.ComputeProbeGridFallbackIndices(Vector3.Zero, positions, null);

        result.X.ShouldBe(4); // dist 1
        result.Y.ShouldBe(1); // dist 2
        result.Z.ShouldBe(3); // dist 3
        result.W.ShouldBe(5); // dist 4
    }

    [Test]
    public void FallbackIndices_PreferredIndices_PrioritizesCellProbes()
    {
        var positions = new List<DefaultRenderPipeline.ProbePositionData>
        {
            new() { Position = new Vector4(1, 0, 0, 1) },  // global nearest but not preferred
            new() { Position = new Vector4(50, 0, 0, 1) }, // far, preferred
            new() { Position = new Vector4(30, 0, 0, 1) }, // mid, preferred
        };
        // Only consider preferred indices (1 and 2)
        var preferred = new List<int> { 1, 2 };
        var result = DefaultRenderPipeline.ComputeProbeGridFallbackIndices(Vector3.Zero, positions, preferred);

        // Should only contain the preferred probes since we pass a non-empty preferred list
        result.X.ShouldBe(2); // dist 30
        result.Y.ShouldBe(1); // dist 50
        result.Z.ShouldBe(-1);
        result.W.ShouldBe(-1);
    }

    [Test]
    public void FallbackIndices_EmptyPreferredList_FallsBackToGlobalScan()
    {
        var positions = new List<DefaultRenderPipeline.ProbePositionData>
        {
            new() { Position = new Vector4(5, 0, 0, 1) },
            new() { Position = new Vector4(2, 0, 0, 1) },
        };
        // null preferred list means empty cell → global scan
        var result = DefaultRenderPipeline.ComputeProbeGridFallbackIndices(Vector3.Zero, positions, null);

        result.X.ShouldBe(1); // dist 2
        result.Y.ShouldBe(0); // dist 5
    }

    [Test]
    public void FallbackIndices_CellCenterOffset_DistancesComputedCorrectly()
    {
        var positions = new List<DefaultRenderPipeline.ProbePositionData>
        {
            new() { Position = new Vector4(0, 0, 0, 1) },  // dist to (10,0,0) = 10
            new() { Position = new Vector4(9, 0, 0, 1) },  // dist to (10,0,0) = 1
            new() { Position = new Vector4(12, 0, 0, 1) }, // dist to (10,0,0) = 2
        };
        var cellCenter = new Vector3(10, 0, 0);
        var result = DefaultRenderPipeline.ComputeProbeGridFallbackIndices(cellCenter, positions, null);

        result.X.ShouldBe(1); // dist 1
        result.Y.ShouldBe(2); // dist 2
        result.Z.ShouldBe(0); // dist 10
    }

    [Test]
    public void FallbackIndices_NoDuplicateIndices()
    {
        // Test that a probe can't appear twice even with preferred list
        var positions = new List<DefaultRenderPipeline.ProbePositionData>
        {
            new() { Position = new Vector4(1, 0, 0, 1) },
            new() { Position = new Vector4(2, 0, 0, 1) },
        };
        var preferred = new List<int> { 0, 0, 0, 1 }; // index 0 repeated
        var result = DefaultRenderPipeline.ComputeProbeGridFallbackIndices(Vector3.Zero, positions, preferred);

        result.X.ShouldBe(0);
        result.Y.ShouldBe(1);
        result.Z.ShouldBe(-1); // no duplicates
        result.W.ShouldBe(-1);
    }

    // ─── Shader source contract tests ───

    [Test]
    public void ForwardLighting_GlobalScanGatedBehindDebugDefine()
    {
        string source = ReadShaderFile("Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl");

        source.ShouldContain("#ifdef XRENGINE_PROBE_DEBUG_FALLBACK");
        source.ShouldContain("#endif // XRENGINE_PROBE_DEBUG_FALLBACK");

        // The grid path should NOT be gated
        source.ShouldContain("XRENGINE_ResolveProbeWeightsGrid");

        // The full tetra+probe scan should be inside the debug block
        int debugStart = source.IndexOf("#ifdef XRENGINE_PROBE_DEBUG_FALLBACK");
        int debugEnd = source.IndexOf("#endif // XRENGINE_PROBE_DEBUG_FALLBACK");
        string debugBlock = source[debugStart..debugEnd];
        debugBlock.ShouldContain("XRENGINE_ResolveProbeWeights");
        debugBlock.ShouldContain("XRENGINE_ComputeBarycentric");
        debugBlock.ShouldContain("TetraCount");
    }

    [Test]
    public void DeferredLightCombine_GlobalScanGatedBehindDebugDefine()
    {
        string source = ReadShaderFile("Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs");

        source.ShouldContain("#ifdef XRENGINE_PROBE_DEBUG_FALLBACK");
        source.ShouldContain("#endif // XRENGINE_PROBE_DEBUG_FALLBACK");

        int debugStart = source.IndexOf("#ifdef XRENGINE_PROBE_DEBUG_FALLBACK");
        int debugEnd = source.IndexOf("#endif // XRENGINE_PROBE_DEBUG_FALLBACK");
        string debugBlock = source[debugStart..debugEnd];
        debugBlock.ShouldContain("ResolveProbeWeights");
        debugBlock.ShouldContain("ComputeBarycentric");
        debugBlock.ShouldContain("TetraCount");
    }

    [Test]
    public void ForwardLighting_GridCellStructHasFallbackIndices()
    {
        string source = ReadShaderFile("Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl");

        source.ShouldContain("struct ProbeGridCell");
        source.ShouldContain("ivec4 OffsetCount;");
        source.ShouldContain("ivec4 FallbackIndices;");
    }

    [Test]
    public void DeferredLightCombine_GridCellStructHasFallbackIndices()
    {
        string source = ReadShaderFile("Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs");

        source.ShouldContain("struct ProbeGridCell");
        source.ShouldContain("ivec4 OffsetCount;");
        source.ShouldContain("ivec4 FallbackIndices;");
    }

    [Test]
    public void PipelineDefineConstant_Exists()
    {
        string source = ReadCSharpFile("XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs");
        source.ShouldContain("ProbeDebugFallbackDefine");
        source.ShouldContain("XRENGINE_PROBE_DEBUG_FALLBACK");
    }

    // ─── Helpers ───

    private static string ReadShaderFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected shader file '{path}' to exist.");
        return File.ReadAllText(path);
    }

    private static string ReadCSharpFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected C# file '{path}' to exist.");
        return File.ReadAllText(path);
    }

    private static string ResolveRepoRoot()
    {
        string? dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "XRENGINE.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        Assert.Fail("Could not find repo root (XRENGINE.slnx).");
        return string.Empty;
    }
}
