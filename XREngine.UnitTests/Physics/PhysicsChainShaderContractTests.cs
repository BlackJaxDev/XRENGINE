using System;
using System.IO;
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
        string componentSource = ReadWorkspaceFile("XRENGINE/Scene/Components/Physics/PhysicsChainComponent.GPU.cs");
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

        source.ShouldContain("layout(row_major, std430, binding = 4) readonly buffer BoneInvBindMatricesBuffer");
        int columnMatrixIndex = source.IndexOf("mat4 boneWorldColumn = mat4(", StringComparison.Ordinal);
        int transposeIndex = source.IndexOf("mat4 boneWorldRow = transpose(boneWorldColumn);", StringComparison.Ordinal);
        int composeIndex = source.IndexOf(
            "mat4 skinMatrix = BoneInvBindMatrices[mapping.BoneMatrixIndex] * boneWorldRow;",
            StringComparison.Ordinal);

        columnMatrixIndex.ShouldBeGreaterThanOrEqualTo(0);
        transposeIndex.ShouldBeGreaterThan(columnMatrixIndex);
        composeIndex.ShouldBeGreaterThan(transposeIndex);
        source.ShouldNotContain("BoneInvBindMatrices[mapping.BoneMatrixIndex] * boneWorld;");
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
        string adapter = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/OpenGLPhysicsChainComputeBackend.cs");

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
    public void Dispatcher_PingPongsCurrentAndPreviousGlobalPaletteAtlases()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs").Replace("\r\n", "\n");

        source.ShouldContain("_gpuDrivenPreviousSkinPaletteBuffer");
        int swapIndex = source.IndexOf(
            "(_gpuDrivenSkinPaletteBuffer, _gpuDrivenPreviousSkinPaletteBuffer) =",
            StringComparison.Ordinal);
        int outputBindIndex = source.IndexOf(
            "_gpuBonePaletteProgram.BindBuffer(_gpuDrivenSkinPaletteBuffer, 3);",
            StringComparison.Ordinal);

        swapIndex.ShouldBeGreaterThanOrEqualTo(0);
        outputBindIndex.ShouldBeGreaterThan(swapIndex);
        source.ShouldContain(
            "SetGpuDrivenSkinPaletteSource(\n                binding.Component,\n                _gpuDrivenSkinPaletteBuffer!,\n                _gpuDrivenPreviousSkinPaletteBuffer,");
        source.ShouldContain("_gpuDrivenPreviousSkinPaletteBuffer?.Dispose();");
        source.ShouldContain("_gpuDrivenPreviousSkinPaletteBuffer = null;");
    }
    [Test]
    public void Dispatcher_BatchesPartialPalettesIntoGlobalAtlasWithoutPerRendererDispatches()
    {
        string component = ReadWorkspaceFile("XRENGINE/Scene/Components/Physics/PhysicsChainComponent.GPU.cs")
            .Replace("\r\n", "\n");
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs")
            .Replace("\r\n", "\n");
        string palette = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Palette.cs")
            .Replace("\r\n", "\n");

        component.ShouldNotContain("if (!state.DrivesCompleteBonePalette)\n                continue;");
        component.ShouldContain("state.DrivesCompleteBonePalette,");
        palette.ShouldContain("if (!binding.DrivesCompleteBonePalette)");
        palette.ShouldContain("TryCopyBuffer(backend, copy, \"partial-palette-seed\")");
        palette.ShouldContain("PartialPaletteSeedCompletionPass");
        dispatcher.ShouldContain("PublishBatchedGpuDrivenBoneMatrices(backend, _dispatchGroup)");
        dispatcher.ShouldContain("if (!request.Component.HasGpuDrivenRenderers || batchedBonePalettePublished)");
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

    private static string ReadGlRenderProgramLinkingSources()
        => string.Join('\n', new[]
        {
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.LinkOrchestration.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.CompileInputs.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.BinaryCacheInteraction.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.AsyncResults.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.HazardDetection.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.LinkDiagnostics.cs"),
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
