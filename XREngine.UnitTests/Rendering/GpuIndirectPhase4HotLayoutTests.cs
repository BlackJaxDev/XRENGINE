using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuIndirectPhase4HotLayoutTests
{
    [Test]
    public void CompactDrawIdState_UsesBoundedUIntBuffers()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Core.cs");

        source.ShouldContain("private XRDataBuffer? _culledSceneToRenderBuffer;");
        source.ShouldContain("private XRDataBuffer? _occlusionCulledBuffer;");
        source.ShouldContain("private static XRDataBuffer MakeCulledSceneToRenderBuffer(");
        source.ShouldContain("EComponentType.UInt,");
        source.ShouldContain("StorageFlags = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read");
        source.ShouldContain("private static uint ComputeBoundedDoublingCapacity(uint currentCapacity, uint minimumRequired)");
    }

    [Test]
    public void CanonicalCullingPath_UsesSceneMetadataAndCompactDrawIds()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.CullingAndSoA.cs");
        string shaderInitialization = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.ShadersAndInit.cs");

        source.ShouldContain("scene.CullControlBuffer.BindTo(_cullingComputeShader, 0);");
        source.ShouldContain("scene.CullBoundsBuffer.BindTo(_cullingComputeShader, 1);");
        source.ShouldContain("_cullingComputeShader.BindBuffer(dst, 2);");
        source.ShouldContain("scene.CullControlBuffer.BindTo(_bvhFrustumCullProgram, 0);");
        source.ShouldContain("scene.CullBoundsBuffer.BindTo(_bvhFrustumCullProgram, 1);");
        source.ShouldContain("_bvhFrustumCullProgram.BindBuffer(dst, 2);");
        source.ShouldContain("FrustumCull(gpuCommands, camera, numCommands);");
        source.ShouldContain("BvhCull(gpuCommands, camera, numCommands);");
        source.ShouldNotContain("ShouldExtractSoAForCurrentPolicy");
        source.ShouldNotContain("ExtractSoA(");
        source.ShouldNotContain("SoACull(");
        source.ShouldNotContain("_extractSoAComputeShader");
        source.ShouldNotContain("_soACullingComputeShader");
        shaderInitialization.ShouldContain("Compute/Culling/GPURenderCulling.comp");
        shaderInitialization.ShouldNotContain("GPURenderExtractSoA");
        shaderInitialization.ShouldNotContain("GPURenderCullingSoA");
    }

    [Test]
    public void OcclusionAndIndirectPath_ConsumeCompactDrawIds()
    {
        string occlusionSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Occlusion.cs");
        string indirectSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");

        occlusionSource.ShouldContain("_hiZOcclusionProgram.BindBuffer(CulledSceneToRenderBuffer!, 0);");
        occlusionSource.ShouldContain("_hiZOcclusionProgram.BindBuffer(_occlusionCulledBuffer!, 1);");
        occlusionSource.ShouldContain("scene.CullControlBuffer.BindTo(_hiZOcclusionProgram, 10u);");
        occlusionSource.ShouldContain("scene.CullBoundsBuffer.BindTo(_hiZOcclusionProgram, 5);");
        occlusionSource.ShouldContain("(_culledSceneToRenderBuffer, _occlusionCulledBuffer) = (_occlusionCulledBuffer, _culledSceneToRenderBuffer);");

        indirectSource.ShouldContain("CulledSceneToRenderBuffer.BindTo(_indirectRenderTaskShader!, 0);");
        indirectSource.ShouldContain("scene.MeshDataBuffer.BindTo(_indirectRenderTaskShader!, 2);");
    }

    [Test]
    public void DrawMetadataAndBoundsStreams_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Commands/GPUIndirectRenderCommand.cs");

        source.ShouldContain("public struct DrawMetadata");
        source.ShouldContain("public struct BoundsGpu");
        source.ShouldContain("public struct TransformGpu");
    }

    [Test]
    public void Phase4_OverflowTailHandling_SourceContracts_ArePresent()
    {
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string passSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");
        string sceneSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.cs");
        string settingsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Settings/RuntimeEngine.Rendering.EngineSettings.cs");

        hybridSource.ShouldContain("private static bool TryReadDrawCount(XRDataBuffer? parameterBuffer, out uint drawCount)");
        hybridSource.ShouldContain("private static void ClearIndirectTail(XRDataBuffer indirectDrawBuffer, uint drawCount, uint maxCommands)");
        hybridSource.ShouldContain("if (!DebugSettings.SkipIndirectTailClear && drawCount < maxCommands)");

        passSource.ShouldContain("Overflow growth policy requested capacity increase");
        passSource.ShouldContain("scene.EnsureCommandCapacity(requestedCapacity)");

        sceneSource.ShouldContain("public uint EnsureCommandCapacity(uint requiredCapacity)");
        settingsSource.ShouldNotContain("EGpuCullingDataLayout");
        settingsSource.ShouldNotContain("GpuCullingDataLayout");
    }

    [Test]
    public void CompactDrawIdShaders_UseSceneStreamsAndBoundedOutput()
    {
        string culling = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Culling/GPURenderCulling.comp");
        string occlusion = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Occlusion/GPURenderOcclusionHiZ.comp");
        string bvh = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_frustum_cull.comp");

        culling.ShouldContain("layout(std430, binding = 0) readonly buffer DrawMetadataBuffer");
        culling.ShouldContain("layout(std430, binding = 1) readonly buffer BoundsBuffer");
        culling.ShouldContain("layout(std430, binding = 2) writeonly buffer VisibleDrawIdsBuffer");
        culling.ShouldContain("outDrawIds[outIndex] = meta.DrawID;");

        occlusion.ShouldContain("layout(std430, binding = 0) buffer InputDrawIdsBuffer");
        occlusion.ShouldContain("layout(std430, binding = 1) buffer OutputDrawIdsBuffer");
        occlusion.ShouldContain("uint outputCapacity = min(uint(max(MaxOutputCommands, 0)), uint(outDrawIds.length()));");
        occlusion.ShouldContain("outDrawIds[outIndex] = drawId;");

        bvh.ShouldContain("layout(std430, binding = 0) readonly buffer DrawMetadataBuffer");
        bvh.ShouldContain("layout(std430, binding = 1) readonly buffer BoundsBuffer");
        bvh.ShouldContain("layout(std430, binding = 2) writeonly buffer VisibleDrawIdsBuffer");
        bvh.ShouldContain("outDrawIds[outIndex] = meta.DrawID;");

        WorkspacePathExists("Build/CommonAssets/Shaders/Compute/Indirect/GPURenderBuildHotCommands.comp").ShouldBeFalse();
        WorkspacePathExists("Build/CommonAssets/Shaders/Compute/Culling/GPURenderExtractSoA.comp").ShouldBeFalse();
        WorkspacePathExists("Build/CommonAssets/Shaders/Compute/Culling/GPURenderCullingSoA.comp").ShouldBeFalse();
        WorkspacePathExists("XREngine.Data/Core/Enums/EGpuCullingDataLayout.cs").ShouldBeFalse();
    }

    [Test]
    public void DrawMetadataAndBoundsLayouts_MatchCurrentGpuAbi()
    {
        int metadataBytes = Marshal.SizeOf<DrawMetadata>();
        int boundsBytes = Marshal.SizeOf<BoundsGpu>();

        metadataBytes.ShouldBe(GPUSceneLayoutContract.DrawMetadataSize);
        boundsBytes.ShouldBe(GPUSceneLayoutContract.BoundsGpuSize);

        int[] commandCounts = [1_000, 10_000, 100_000];
        foreach (int count in commandCounts)
        {
            long metadataStreamBytes = (long)count * metadataBytes * 2L;
            long boundsStreamBytes = (long)count * boundsBytes * 2L;

            TestContext.WriteLine($"GPU scene stream model count={count}: metadata={metadataStreamBytes} bytes bounds={boundsStreamBytes} bytes");
            boundsStreamBytes.ShouldBe(metadataStreamBytes);
        }
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected file does not exist: {fullPath}");
        return File.ReadAllText(fullPath).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static bool WorkspacePathExists(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return true;

            dir = dir.Parent;
        }

        return false;
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

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }
}

