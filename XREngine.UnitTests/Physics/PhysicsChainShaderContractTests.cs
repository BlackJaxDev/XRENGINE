using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainShaderContractTests
{
    [TestCase("PhysicsChain.comp")]
    [TestCase("SkipUpdateParticles.comp")]
    public void PhysicsChainDispatchShaders_GuardTreeInvocationsBeforeBufferReads(string shaderFileName)
    {
        string source = ReadPhysicsChainShader(shaderFileName).Replace("\r\n", "\n");

        source.ShouldContain("uniform int ParticleCount;");
        source.ShouldContain("uniform int TreeCount;");
        int treeIndex = source.IndexOf("gl_GlobalInvocationID.x", StringComparison.Ordinal);
        int guardIndex = source.IndexOf("if (ParticleCount <= 0 || TreeCount <= 0", StringComparison.Ordinal);
        int firstTreeRead = source.IndexOf("TreeParams[treeId]", StringComparison.Ordinal);
        int firstParticleStaticRead = source.IndexOf("ParticleStatics[pid]", StringComparison.Ordinal);
        int firstParticleRead = source.IndexOf("Particles[pid]", StringComparison.Ordinal);
        int firstTransformRead = source.IndexOf("TransformMatrices[pid]", StringComparison.Ordinal);

        treeIndex.ShouldBeGreaterThanOrEqualTo(0);
        guardIndex.ShouldBeGreaterThan(treeIndex);
        firstTreeRead.ShouldBeGreaterThan(guardIndex);
        firstParticleStaticRead.ShouldBeGreaterThan(guardIndex);
        firstParticleRead.ShouldBeGreaterThan(guardIndex);
        firstTransformRead.ShouldBeGreaterThan(guardIndex);
    }

    [TestCase("PhysicsChain.comp")]
    [TestCase("PhysicsChainBranched.comp")]
    [TestCase("SkipUpdateParticles.comp")]
    public void PhysicsChainDispatchShaders_DoNotShadowAutoUniformNamesInStructFields(string shaderFileName)
    {
        string source = ReadPhysicsChainShader(shaderFileName).Replace("\r\n", "\n");

        source.ShouldContain("uniform int ParticleCount;");
        source.ShouldNotContain("\n    int ParticleCount;");
        source.ShouldNotContain("\n    uint ParticleCount;");
    }

    [Test]
    public void PhysicsChainDispatchers_SetParticleAndTreeCountUniformsBeforeDispatch()
    {
        string componentSource = ReadWorkspaceFile("XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainComponent.GPU.cs");
        string dispatcherSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs")
            + ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Kernels.cs");

        componentSource.ShouldNotContain(".DispatchCompute(");
        dispatcherSource.ShouldContain("program.Uniform(\"ParticleCount\", TotalParticleCount);");
        dispatcherSource.ShouldContain("program.Uniform(\"TreeCount\", TotalTreeCount);");
    }

    [Test]
    public void MainPhysicsShader_OwnsEachTreeInOneInvocation()
    {
        string source = ReadPhysicsChainShader("PhysicsChain.comp").Replace("\r\n", "\n");

        source.ShouldContain("uint dispatchIndex = gl_GlobalInvocationID.x;");
        source.ShouldContain("ActiveTreeIds[ActiveTreeIdBase + dispatchIndex]");
        source.ShouldContain("PerTreeParams tp = TreeParams[treeId];");
        source.ShouldContain("for (int pid = particleStart; pid < particleEnd; ++pid)");
        source.ShouldContain("parents precede children");
        source.ShouldContain("if (parentIndex < particleStart || parentIndex >= pid)");
        source.ShouldContain("if (parentIndex >= particleStart && parentIndex < pid)");
        source.ShouldNotContain("uint pid = gl_GlobalInvocationID.x;");
        source.ShouldNotContain("ParticleState parentStatic");
    }

    [Test]
    public void MainPhysicsShader_UsesCurrentBufferBindings()
    {
        string source = ReadPhysicsChainShader("PhysicsChain.comp").Replace("\r\n", "\n");

        source.ShouldContain("layout(std430, binding = 0) buffer ParticlesBuffer");
        source.ShouldContain("layout(std430, binding = 1) readonly buffer ParticleStaticBuffer");
        source.ShouldContain("layout(std430, binding = 3) buffer TransformMatricesBuffer");
        source.ShouldContain("layout(std430, binding = 4) buffer CollidersBuffer");
        source.ShouldContain("layout(std430, binding = 5) readonly buffer PerTreeParamsBuffer");
        source.ShouldNotContain("binding = 2");
    }

    [Test]
    public void BonePaletteShader_ComposesSystemNumericsRowVectorMatrices()
    {
        string source = ReadPhysicsChainShader("PhysicsChainBonePalette.comp").Replace("\r\n", "\n");
        string bonePose = ReadPhysicsChainShader("PhysicsChainBonePose.glslinc").Replace("\r\n", "\n");

        source.ShouldContain("#include \"Compute/PhysicsChain/PhysicsChainBonePose.glslinc\"");
        source.ShouldContain("layout(row_major, std430, binding = 4) readonly buffer BoneInvBindMatricesBuffer");
        // The include builds the bone world matrix in GLSL column-vector form.
        bonePose.ShouldContain("mat4 composeChainBoneWorld(");
        bonePose.ShouldContain(
            "return mat4(vec4(basis[0], 0.0), vec4(basis[1], 0.0), vec4(basis[2], 0.0), vec4(position, 1.0));");
        int columnMatrixIndex = source.IndexOf("mat4 boneWorldColumn = composeChainBoneWorld(", StringComparison.Ordinal);
        int transposeIndex = source.IndexOf("mat4 boneWorldRow = transpose(boneWorldColumn);", StringComparison.Ordinal);
        int composeIndex = source.IndexOf(
            "mat4 skinMatrix = BoneInvBindMatrices[mapping.BoneMatrixIndex] * boneWorldRow;",
            StringComparison.Ordinal);
        int writeIndex = source.IndexOf(
            "writeSkinPalette(uint(mapping.BoneMatrixIndex), skinMatrix);",
            StringComparison.Ordinal);

        columnMatrixIndex.ShouldBeGreaterThanOrEqualTo(0);
        transposeIndex.ShouldBeGreaterThan(columnMatrixIndex);
        composeIndex.ShouldBeGreaterThan(transposeIndex);
        writeIndex.ShouldBeGreaterThan(composeIndex);
        source.ShouldNotContain("BoneInvBindMatrices[mapping.BoneMatrixIndex] * boneWorld;");

        // Palette row c is column c of the row-vector skin matrix, as in SkinPaletteMatrix.FromRowVectorMatrix.
        source.ShouldContain("SkinPaletteRows[rowBase + 0u] = skinMatrix[0];");
        source.ShouldContain("SkinPaletteRows[rowBase + 1u] = skinMatrix[1];");
        source.ShouldContain("SkinPaletteRows[rowBase + 2u] = skinMatrix[2];");
        XREngine.Rendering.SkinPaletteMatrix palette = XREngine.Rendering.SkinPaletteMatrix.FromRowVectorMatrix(
            Matrix4x4.CreateTranslation(5.0f, 6.0f, 7.0f));
        palette.Row0.W.ShouldBe(5.0f);
        palette.Row1.W.ShouldBe(6.0f);
        palette.Row2.W.ShouldBe(7.0f);
    }

    [Test]
    public void PhysicsChainGpuRecords_MatchStd430Layout()
    {
        Marshal.SizeOf<GPUPhysicsChainDispatcher.GPUParticleData>().ShouldBe(64);
        OffsetOf<GPUPhysicsChainDispatcher.GPUParticleData>(nameof(GPUPhysicsChainDispatcher.GPUParticleData.Position)).ShouldBe(0);
        OffsetOf<GPUPhysicsChainDispatcher.GPUParticleData>(nameof(GPUPhysicsChainDispatcher.GPUParticleData.PrevPosition)).ShouldBe(16);
        OffsetOf<GPUPhysicsChainDispatcher.GPUParticleData>(nameof(GPUPhysicsChainDispatcher.GPUParticleData.IsColliding)).ShouldBe(32);
        OffsetOf<GPUPhysicsChainDispatcher.GPUParticleData>(nameof(GPUPhysicsChainDispatcher.GPUParticleData.PreviousPhysicsPosition)).ShouldBe(48);

        Marshal.SizeOf<GPUPhysicsChainDispatcher.GPUParticleStaticData>().ShouldBe(64);
        OffsetOf<GPUPhysicsChainDispatcher.GPUParticleStaticData>(nameof(GPUPhysicsChainDispatcher.GPUParticleStaticData.ParentIndex)).ShouldBe(16);
        OffsetOf<GPUPhysicsChainDispatcher.GPUParticleStaticData>(nameof(GPUPhysicsChainDispatcher.GPUParticleStaticData.Inert)).ShouldBe(32);
        OffsetOf<GPUPhysicsChainDispatcher.GPUParticleStaticData>(nameof(GPUPhysicsChainDispatcher.GPUParticleStaticData.TreeIndex)).ShouldBe(48);

        Marshal.SizeOf<GPUPhysicsChainDispatcher.GPUColliderData>().ShouldBe(64);
        OffsetOf<GPUPhysicsChainDispatcher.GPUColliderData>(nameof(GPUPhysicsChainDispatcher.GPUColliderData.Params)).ShouldBe(16);
        OffsetOf<GPUPhysicsChainDispatcher.GPUColliderData>(nameof(GPUPhysicsChainDispatcher.GPUColliderData.Orientation)).ShouldBe(32);
        OffsetOf<GPUPhysicsChainDispatcher.GPUColliderData>(nameof(GPUPhysicsChainDispatcher.GPUColliderData.Type)).ShouldBe(48);

        Marshal.SizeOf<GPUPhysicsChainDispatcher.GPUPerTreeParams>().ShouldBe(96);
        OffsetOf<GPUPhysicsChainDispatcher.GPUPerTreeParams>(nameof(GPUPhysicsChainDispatcher.GPUPerTreeParams.Force)).ShouldBe(16);
        OffsetOf<GPUPhysicsChainDispatcher.GPUPerTreeParams>(nameof(GPUPhysicsChainDispatcher.GPUPerTreeParams.Gravity)).ShouldBe(32);
        OffsetOf<GPUPhysicsChainDispatcher.GPUPerTreeParams>(nameof(GPUPhysicsChainDispatcher.GPUPerTreeParams.ObjectMove)).ShouldBe(48);
        OffsetOf<GPUPhysicsChainDispatcher.GPUPerTreeParams>(nameof(GPUPhysicsChainDispatcher.GPUPerTreeParams.RestGravity)).ShouldBe(64);
        OffsetOf<GPUPhysicsChainDispatcher.GPUPerTreeParams>(nameof(GPUPhysicsChainDispatcher.GPUPerTreeParams.ParticleCount)).ShouldBe(80);
        OffsetOf<GPUPhysicsChainDispatcher.GPUPerTreeParams>(nameof(GPUPhysicsChainDispatcher.GPUPerTreeParams.LoopCount)).ShouldBe(84);
    }

    [Test]
    public void Dispatcher_PublishesUnsupportedBackendStatusBeforeReturning()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs");

        int evaluateIndex = source.IndexOf("IPhysicsChainComputeBackend? backend = ResolveComputeBackend(", StringComparison.Ordinal);
        int gateIndex = source.IndexOf("if (backend is null)", evaluateIndex, StringComparison.Ordinal);
        int returnIndex = source.IndexOf("return;", gateIndex, StringComparison.Ordinal);

        evaluateIndex.ShouldBeGreaterThanOrEqualTo(0);
        gateIndex.ShouldBeGreaterThan(evaluateIndex);
        returnIndex.ShouldBeGreaterThan(gateIndex);
        source.ShouldContain("PublishBackendStatus(EvaluateBackendCapability(");
        source.ShouldContain("CpuFallbackUsed: false");
        source.ShouldContain("no CPU fallback was used");
    }

    [Test]
    public void Dispatcher_ConfinesOpenGlOperationsToBackendAdapter()
    {
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs");
        string adapter = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/PhysicsCompute/OpenGLPhysicsChainComputeBackend.cs");

        dispatcher.ShouldNotContain("OpenGLRenderer");
        dispatcher.ShouldNotContain("RawGL");
        dispatcher.ShouldNotContain("Silk.NET.OpenGL");
        dispatcher.ShouldContain("IPhysicsChainComputeBackend");
        dispatcher.ShouldContain("PhysicsChainComputeBufferCopy");

        adapter.ShouldContain("OpenGLRenderer");
        adapter.ShouldContain("CopyNamedBufferSubData");
        adapter.ShouldContain("GetBufferSubData");
        adapter.ShouldContain("_renderer.InsertGpuFence()");
    }

    [Test]
    public void Dispatcher_KeepsCurrentAndPreviousPaletteAtlasesPerOutputPage()
    {
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs")
            .Replace("\r\n", "\n");
        string outputPages = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.OutputPages.cs")
            .Replace("\r\n", "\n");

        // Each output page owns one current and one previous palette atlas.
        outputPages.ShouldContain("public XRDataBuffer<SkinPaletteMatrix>? CurrentPalette;");
        outputPages.ShouldContain("public XRDataBuffer<SkinPaletteMatrix>? PreviousPalette;");
        dispatcher.ShouldContain("\"PhysicsChainGlobalPreviousSkinPalette\"");

        // The palette pass writes the current atlas of the page that it writes.
        // Only the unpaged atlases swap; a page keeps its own pair.
        int pageCurrentIndex = dispatcher.IndexOf(
            "_gpuDrivenSkinPaletteBuffer = outputPage.CurrentPalette;",
            StringComparison.Ordinal);
        int pagePreviousIndex = dispatcher.IndexOf(
            "_gpuDrivenPreviousSkinPaletteBuffer = outputPage.PreviousPalette;",
            StringComparison.Ordinal);
        int unpagedSwapIndex = dispatcher.IndexOf(
            "else if (outputPage is null)\n            (_gpuDrivenSkinPaletteBuffer, _gpuDrivenPreviousSkinPaletteBuffer) =",
            StringComparison.Ordinal);
        int outputBindIndex = dispatcher.IndexOf(
            "_gpuBonePaletteProgram.BindBuffer(_gpuDrivenSkinPaletteBuffer, 3);",
            StringComparison.Ordinal);
        pageCurrentIndex.ShouldBeGreaterThanOrEqualTo(0);
        pagePreviousIndex.ShouldBeGreaterThan(pageCurrentIndex);
        unpagedSwapIndex.ShouldBeGreaterThan(pagePreviousIndex);
        outputBindIndex.ShouldBeGreaterThan(unpagedSwapIndex);
        dispatcher.ShouldContain("outputPage.CurrentPalette = _gpuDrivenSkinPaletteBuffer;");
        dispatcher.ShouldContain("outputPage.PreviousPalette = _gpuDrivenPreviousSkinPaletteBuffer;");

        // The previous atlas is filled after the current palette pass and before the bounds pass reads both.
        int publishStart = dispatcher.IndexOf("private bool PublishRegisteredOutputs(", StringComparison.Ordinal);
        publishStart.ShouldBeGreaterThanOrEqualTo(0);
        int palettePassIndex = dispatcher.IndexOf(
            "PublishBatchedGpuDrivenBoneMatrices(backend, _outputPublicationRequests)",
            publishStart,
            StringComparison.Ordinal);
        palettePassIndex.ShouldBeGreaterThan(publishStart);
        int paletteBarrierIndex = dispatcher.IndexOf(
            "TryCompletePass(backend, BonePaletteCompletionPass)",
            palettePassIndex,
            StringComparison.Ordinal);
        paletteBarrierIndex.ShouldBeGreaterThan(palettePassIndex);
        int previousCopyIndex = dispatcher.IndexOf(
            "CopyPreviousOutputPalette(backend,",
            paletteBarrierIndex,
            StringComparison.Ordinal);
        previousCopyIndex.ShouldBeGreaterThan(paletteBarrierIndex);
        int boundsIndex = dispatcher.IndexOf(
            "PublishGpuDrivenBounds(backend, _outputPublicationRequests)",
            previousCopyIndex,
            StringComparison.Ordinal);
        boundsIndex.ShouldBeGreaterThan(previousCopyIndex);

        // Matched renderers copy the previous palette from the current palette of the published page.
        // Other renderers seed it from the current palette of the written page and reset their history.
        int copyStart = outputPages.IndexOf("private bool CopyPreviousOutputPalette(", StringComparison.Ordinal);
        copyStart.ShouldBeGreaterThanOrEqualTo(0);
        int sourcePageIndex = outputPages.IndexOf(
            "PhysicsChainOutputPage? source = _publishedOutputPageIndex >= 0",
            copyStart,
            StringComparison.Ordinal);
        sourcePageIndex.ShouldBeGreaterThan(copyStart);
        int sourcePaletteIndex = outputPages.IndexOf(
            "XRDataBuffer<SkinPaletteMatrix>? sourcePalette = source?.CurrentPalette;",
            sourcePageIndex,
            StringComparison.Ordinal);
        sourcePaletteIndex.ShouldBeGreaterThan(sourcePageIndex);
        int destinationPaletteIndex = outputPages.IndexOf(
            "XRDataBuffer<SkinPaletteMatrix> previousPalette = destination.PreviousPalette;",
            sourcePaletteIndex,
            StringComparison.Ordinal);
        destinationPaletteIndex.ShouldBeGreaterThan(sourcePaletteIndex);
        int publishedCopyIndex = outputPages.IndexOf(
            "TryCopyPreviousPaletteRanges(backend, sourcePalette, previousPalette)",
            destinationPaletteIndex,
            StringComparison.Ordinal);
        publishedCopyIndex.ShouldBeGreaterThan(destinationPaletteIndex);
        int seedCopyIndex = outputPages.IndexOf(
            "TryCopyPreviousPaletteRanges(backend, destination.CurrentPalette, previousPalette)",
            publishedCopyIndex,
            StringComparison.Ordinal);
        seedCopyIndex.ShouldBeGreaterThan(publishedCopyIndex);
        outputPages.IndexOf("destination.PaletteHistoryResetRenderers.Add(", copyStart, StringComparison.Ordinal)
            .ShouldBeGreaterThan(copyStart);

        // Disposal releases the atlases of every page and the unpaged pair.
        outputPages.ShouldContain("page.CurrentPalette?.Dispose();");
        outputPages.ShouldContain("page.PreviousPalette?.Dispose();");
        outputPages.ShouldContain("_unpagedPreviousSkinPaletteBuffer?.Dispose();");
        dispatcher.ShouldContain("DisposeUnpagedPaletteBuffers();");
        dispatcher.ShouldContain("_gpuDrivenPreviousSkinPaletteBuffer = null;");
    }

    [Test]
    public void Dispatcher_BatchesPartialPalettesIntoGlobalAtlasWithoutPerRendererDispatches()
    {
        string component = ReadWorkspaceFile("XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainComponent.GPU.cs")
            .Replace("\r\n", "\n");
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs")
            .Replace("\r\n", "\n");
        string palette = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Palette.cs")
            .Replace("\r\n", "\n");

        component.ShouldNotContain("if (!state.DrivesCompleteBonePalette)\n                continue;");
        string renderingBridge = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/RuntimePhysicsChainRenderingBridge.cs")
            .Replace("\r\n", "\n");
        renderingBridge.ShouldContain("state.BoneMatrixElementCount, state.Complete,");
        palette.ShouldContain("if (!binding.DrivesCompleteBonePalette)");
        palette.ShouldContain("TryCopyBuffer(backend, copy, \"partial-palette-seed\")");
        palette.ShouldContain("PartialPaletteSeedCompletionPass");
        // Every registered request with GPU-driven renderers joins one output publication.
        dispatcher.ShouldContain("&& request.Component.HasGpuDrivenRenderers)");
        dispatcher.ShouldContain("_outputPublicationRequests.Add(request);");
        dispatcher.ShouldContain(
            "request.Component.AppendBatchedGpuDrivenBonePaletteBindings(request.ParticleOffset, _gpuDrivenPaletteBindings);");
        dispatcher.ShouldContain("PublishBatchedGpuDrivenBoneMatrices(backend, _outputPublicationRequests)");

        // One palette dispatch covers the mappings of all bindings.
        dispatcher.ShouldContain("_gpuBonePaletteProgram.Uniform(\"mappingCount\", _gpuDrivenBoneMappings.Count);");

        // No dispatcher path runs a per-component palette publication.
        ReadDispatcherPartialSources().ShouldNotContain("PublishGpuDrivenBoneMatrices(");
        dispatcher.ShouldNotContain("batchedBonePalettePublished");
        dispatcher.ShouldNotContain("includeCompletePalettes: !batchedBonePalettePublished");
    }

    [Test]
    public void OpenGlRenderProgram_GeneratesBuffersBeforeComputeBinding()
    {
        string source = ReadGlRenderProgramLinkingSources();

        source.ShouldContain("Renderer.GetOrCreateAPIRenderObject(buffer, generateNow: true)");
    }

    private static int OffsetOf<T>(string fieldName)
        => Marshal.OffsetOf<T>(fieldName).ToInt32();

    private static string ReadPhysicsChainShader(string fileName)
        => ReadWorkspaceFile($"Build/CommonAssets/Shaders/Compute/PhysicsChain/{fileName}");

    /// <summary>Reads all partial source files of the dispatcher in ordinal path order.</summary>
    private static string ReadDispatcherPartialSources()
    {
        string directory = Path.Combine(ResolveRepoRoot(), "XREngine.Runtime.Rendering", "Rendering", "PhysicsCompute");
        string[] paths = Directory.GetFiles(directory, "GPUPhysicsChainDispatcher*.cs");
        Array.Sort(paths, StringComparer.Ordinal);
        paths.Length.ShouldBeGreaterThan(1);

        string[] sources = new string[paths.Length];
        for (int index = 0; index < paths.Length; ++index)
            sources[index] = File.ReadAllText(paths[index]).Replace("\r\n", "\n");
        return string.Join('\n', sources);
    }

    private static string ReadGlRenderProgramLinkingSources()
        => string.Join('\n', new[]
        {
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.LinkOrchestration.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.CompileInputs.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.BinaryCacheInteraction.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.AsyncResults.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.HazardDetection.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.LinkDiagnostics.cs"),
        });

    private static string ReadWorkspaceFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");
        return File.ReadAllText(path);
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
