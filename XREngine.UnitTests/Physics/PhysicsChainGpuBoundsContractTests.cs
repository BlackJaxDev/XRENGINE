using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainGpuBoundsContractTests
{
    [Test]
    public void BoundsRecords_MatchShaderStd430Layout()
    {
        string boundsShader = ReadWorkspaceFile(
            "Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChainBounds.comp");
        string copyShader = ReadWorkspaceFile(
            "Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChainBoundsToScene.comp");

        // std430 BoundsWorkItem: two vec4 values at 0 and 16, then four uint values at 32 to 44.
        Marshal.SizeOf<GPUPhysicsChainDispatcher.PhysicsChainGpuBoundsWorkItem>().ShouldBe(48);
        var workItem = new GPUPhysicsChainDispatcher.PhysicsChainGpuBoundsWorkItem(
            new Vector4(1.0f, 2.0f, 3.0f, 4.0f),
            new Vector4(5.0f, 6.0f, 7.0f, 8.0f),
            PaletteBase: 11u,
            PaletteCount: 12u,
            BoundsSlot: 13u,
            Flags: 14u);
        ReadOnlySpan<byte> workItemBytes = MemoryMarshal.AsBytes(new[] { workItem }.AsSpan());
        workItemBytes.Length.ShouldBe(48);
        MemoryMarshal.Read<Vector4>(workItemBytes).ShouldBe(new Vector4(1.0f, 2.0f, 3.0f, 4.0f));
        MemoryMarshal.Read<Vector4>(workItemBytes[16..]).ShouldBe(new Vector4(5.0f, 6.0f, 7.0f, 8.0f));
        MemoryMarshal.Read<uint>(workItemBytes[32..]).ShouldBe(11u);
        MemoryMarshal.Read<uint>(workItemBytes[36..]).ShouldBe(12u);
        MemoryMarshal.Read<uint>(workItemBytes[40..]).ShouldBe(13u);
        MemoryMarshal.Read<uint>(workItemBytes[44..]).ShouldBe(14u);
        ShouldDeclareStructFieldsInOrder(boundsShader, "struct BoundsWorkItem",
            "vec4 LocalMinimum;", "vec4 LocalMaximum;", "uint PaletteBase;",
            "uint PaletteCount;", "uint BoundsSlot;", "uint Flags;");
        GPUPhysicsChainDispatcher.BoundsWorkItemRejectedMaterialContract.ShouldBe(1u);
        boundsShader.ShouldContain("const uint BoundsWorkItemRejectedMaterialContract = 1u;");

        Marshal.SizeOf<GPUPhysicsChainDispatcher.PhysicsChainGpuBoundsCopyItem>().ShouldBe(8);
        var copyItem = new GPUPhysicsChainDispatcher.PhysicsChainGpuBoundsCopyItem(
            SourceSlot: 21u,
            TargetCommandSlot: 22u);
        ReadOnlySpan<byte> copyItemBytes = MemoryMarshal.AsBytes(new[] { copyItem }.AsSpan());
        copyItemBytes.Length.ShouldBe(8);
        MemoryMarshal.Read<uint>(copyItemBytes).ShouldBe(21u);
        MemoryMarshal.Read<uint>(copyItemBytes[4..]).ShouldBe(22u);
        ShouldDeclareStructFieldsInOrder(copyShader, "struct BoundsCopyItem",
            "uint SourceSlot;", "uint TargetCommandSlot;");

        Marshal.SizeOf<XREngine.Rendering.AdvancedGpuBoundsPatchRoute>().ShouldBe(32);
    }

    [Test]
    public void BoundsShader_EnvelopesMeshBoxUnderCurrentAndPreviousPalettes()
    {
        string source = ReadWorkspaceFile(
            "Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChainBounds.comp");
        string dispatcher = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Bounds.cs");

        // One workgroup reduces the bounds of one mesh.
        source.ShouldContain("uint workItemIndex = gl_WorkGroupID.x;");
        source.ShouldContain("layout(std430, binding = 1) readonly buffer CurrentPaletteBuffer");
        source.ShouldContain("layout(std430, binding = 2) readonly buffer PreviousPaletteBuffer");
        dispatcher.ShouldContain("boundsProgram.BindBuffer(outputPage.CurrentPalette, 1);");
        dispatcher.ShouldContain("boundsProgram.BindBuffer(outputPage.PreviousPalette, 2);");

        // Each current and previous palette matrix moves the mesh-local box to a conservative world box.
        source.ShouldContain("vec3 worldExtent = vec3(dot(abs(row0.xyz), halfExtent),");
        source.ShouldContain("IncludePaletteBox(CurrentPaletteRows[row], CurrentPaletteRows[row + 1u],");
        source.ShouldContain("IncludePaletteBox(PreviousPaletteRows[row], PreviousPaletteRows[row + 1u],");

        // The material vertex-effect padding grows the reduced box.
        source.ShouldContain("finalMinimum -= numericMargin + vec3(effectPadding);");
        source.ShouldContain("finalMaximum += numericMargin + vec3(effectPadding);");
        source.ShouldContain("item.BoundsSlot * 2u");
        source.ToLowerInvariant().ShouldNotContain("readback");
    }

    [Test]
    public void Dispatcher_PublishesStableBoundsSlotsDirectlyToGpuScene()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Bounds.cs");
        string copyShader = ReadWorkspaceFile(
            "Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChainBoundsToScene.comp");

        source.ShouldContain("_gpuBoundsSlotAllocator.Acquire(key, 1u)");

        // A scene route snapshot is copied again only when the scene publishes new routes.
        int generationCheckIndex = source.IndexOf(
            "routes.PublicationGeneration != scene.RendererCommandMaterialPublicationGeneration",
            StringComparison.Ordinal);
        int captureIndex = source.IndexOf(
            "scene.CaptureRendererCommandIndices(routes);",
            StringComparison.Ordinal);
        generationCheckIndex.ShouldBeGreaterThanOrEqualTo(0);
        captureIndex.ShouldBeGreaterThan(generationCheckIndex);
        source.ShouldContain("routes.TryGetCommandIndices(binding.Renderer, _gpuBoundsCommandScratch)");

        source.ShouldContain("scene.SetRendererOwnsGpuCopiedAabb(binding.Renderer, true)");
        source.ShouldContain("scene.CommandAabbBuffer");
        source.ShouldContain("UsesCpuReadback: false");
        source.ShouldNotContain("WaitForGpu");
        copyShader.ShouldContain("ChainBoundsBits[source].xyz");
        copyShader.ShouldContain("ChainBoundsBits[source + 1u].xyz");
        copyShader.ShouldContain("isnan");
        copyShader.ShouldContain("isinf");
    }

    /// <summary>
    /// Asserts that a GLSL struct declares the given fields in order, inside its body.
    /// </summary>
    private static void ShouldDeclareStructFieldsInOrder(string source, string structHeader, params string[] fieldDeclarations)
    {
        int headerIndex = source.IndexOf(structHeader, StringComparison.Ordinal);
        headerIndex.ShouldBeGreaterThanOrEqualTo(0, $"Expected '{structHeader}' in the shader.");
        int bodyEnd = source.IndexOf("};", headerIndex, StringComparison.Ordinal);
        bodyEnd.ShouldBeGreaterThan(headerIndex, $"Expected the body of '{structHeader}' to end.");

        int previousIndex = headerIndex;
        foreach (string field in fieldDeclarations)
        {
            int fieldIndex = source.IndexOf(field, previousIndex, StringComparison.Ordinal);
            fieldIndex.ShouldBeGreaterThan(previousIndex, $"Expected '{field}' in order in '{structHeader}'.");
            fieldIndex.ShouldBeLessThan(bodyEnd, $"Expected '{field}' inside '{structHeader}'.");
            previousIndex = fieldIndex;
        }
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    private static string ResolveRepoRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
