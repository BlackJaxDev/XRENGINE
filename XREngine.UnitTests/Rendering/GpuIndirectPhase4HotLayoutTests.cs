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
    public void Phase4_CoreHotLayoutState_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Core.cs");

        source.ShouldContain("public bool EnableHotCommandLayout { get; set; } = true;");
        source.ShouldContain("private XRDataBuffer? _sourceHotCommandBuffer;");
        source.ShouldContain("private XRDataBuffer? _culledHotCommandBuffer;");
        source.ShouldContain("private XRDataBuffer? _occlusionCulledHotBuffer;");
        source.ShouldContain("private bool _sourceCommandsUseHotLayout;");
        source.ShouldContain("private bool _culledHotCommandsValid;");
        source.ShouldContain("private static XRDataBuffer MakeHotCommandBuffer(string name, uint capacity)");
        source.ShouldContain("private static bool IsShippingHotOnlyProfile()", Case.Insensitive);
        source.ShouldContain("private static bool IsHotCommandLayoutEnabled()", Case.Insensitive);
        source.ShouldContain("private static bool IsHotCommandLayoutRequired()", Case.Insensitive);
        source.ShouldContain("private static uint ComputeBoundedDoublingCapacity(uint currentCapacity, uint minimumRequired)");
    }

    [Test]
    public void Phase4_CanonicalCullingHotPath_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.CullingAndSoA.cs");
        string shaderInitialization = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.ShadersAndInit.cs");

        source.ShouldContain("private void BuildSourceHotCommandBuffer(GPUScene scene, uint inputCount)");
        source.ShouldContain("_buildHotCommandsProgram.Uniform(\"InputCount\", (int)inputCount);");
        source.ShouldContain("_cullingComputeShader.Uniform(\"UseHotCommands\", useHotCommands ? 1 : 0);");
        source.ShouldContain("_cullingComputeShader.BindBuffer(_sourceHotCommandBuffer!, 9);");
        source.ShouldContain("_cullingComputeShader.BindBuffer(_culledHotCommandBuffer!, 10);");
        source.ShouldContain("_bvhFrustumCullProgram.Uniform(\"UseHotCommands\", useHotCommands ? 1u : 0u);");
        source.ShouldContain("_bvhFrustumCullProgram.BindBuffer(dst, 2);");
        source.ShouldContain("_bvhFrustumCullProgram.BindBuffer(_culledHotCommandBuffer!, 10);");
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
        source.ShouldContain("ShippingFast profile requires hot-command layout for frustum culling.");
        source.ShouldContain("ShippingFast profile requires hot-command layout for BVH culling.");
    }

    [Test]
    public void Phase4_OcclusionAndIndirectHotPath_SourceContracts_ArePresent()
    {
        string occlusionSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Occlusion.cs");
        string indirectSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");

        occlusionSource.ShouldContain("_hiZOcclusionProgram.Uniform(\"UseHotCommands\", useHotCommands ? 1 : 0);");
        occlusionSource.ShouldContain("_hiZOcclusionProgram.BindBuffer(_culledHotCommandBuffer!, 9);");
        occlusionSource.ShouldContain("_hiZOcclusionProgram.BindBuffer(_occlusionCulledHotBuffer!, 10);");
        occlusionSource.ShouldContain("(_culledHotCommandBuffer, _occlusionCulledHotBuffer) = (_occlusionCulledHotBuffer, _culledHotCommandBuffer);");

        indirectSource.ShouldContain("_indirectRenderTaskShader.Uniform(\"UseHotCommands\", _culledCommandsUseHotLayout ? 1 : 0);");
        indirectSource.ShouldContain("? _culledHotCommandBuffer");
        indirectSource.ShouldContain(": CulledSceneToRenderBuffer).BindTo(_indirectRenderTaskShader!, 9);");
        indirectSource.ShouldContain("_buildHotCommandsProgram.Uniform(\"InputCount\", (int)inputCount);");
        indirectSource.ShouldContain("ShippingFast requires hot command layout", Case.Insensitive);
    }

    [Test]
    public void Phase4_ColdPayloadMigration_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Commands/GPUIndirectRenderCommand.cs");

        source.ShouldContain("public struct GPUIndirectRenderCommandCold");
        source.ShouldContain("public GPUIndirectRenderCommandCold ToCold()");
        source.ShouldContain("public static GPUIndirectRenderCommand FromHotCold");
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
    public void Phase4_ShaderHotLayoutContracts_ArePresent()
    {
        string buildHot = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Indirect/GPURenderBuildHotCommands.comp");
        string culling = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Culling/GPURenderCulling.comp");
        string occlusion = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Occlusion/GPURenderOcclusionHiZ.comp");
        string bvh = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_frustum_cull.comp");

        buildHot.ShouldContain("uniform int InputCount;");
        buildHot.ShouldContain("const uint HOT_UINTS = 20u;");

        culling.ShouldContain("layout(std430, binding = 0) readonly buffer DrawMetadataBuffer");
        culling.ShouldContain("layout(std430, binding = 1) readonly buffer BoundsBuffer");
        culling.ShouldContain("layout(std430, binding = 2) writeonly buffer CulledCommandsBuffer");
        culling.ShouldContain("layout(std430, binding = 10) writeonly buffer CulledHotCommandsBuffer");
        culling.ShouldContain("uniform int UseHotCommands;");

        occlusion.ShouldContain("layout(std430, binding = 9) readonly buffer InputHotCommandsBuffer");
        occlusion.ShouldContain("layout(std430, binding = 10) writeonly buffer OutputHotCommandsBuffer");
        occlusion.ShouldContain("uniform int UseHotCommands;");

        bvh.ShouldContain("layout(std430, binding = 0) readonly buffer DrawMetadataBuffer");
        bvh.ShouldContain("layout(std430, binding = 1) readonly buffer BoundsBuffer");
        bvh.ShouldContain("layout(std430, binding = 2) writeonly buffer CulledCommandsBuffer");
        bvh.ShouldContain("layout(std430, binding = 10) writeonly buffer CulledHotCommandsBuffer");
        bvh.ShouldContain("uniform uint UseHotCommands;");

        WorkspacePathExists("Build/CommonAssets/Shaders/Compute/Culling/GPURenderExtractSoA.comp").ShouldBeFalse();
        WorkspacePathExists("Build/CommonAssets/Shaders/Compute/Culling/GPURenderCullingSoA.comp").ShouldBeFalse();
        WorkspacePathExists("XREngine.Data/Core/Enums/EGpuCullingDataLayout.cs").ShouldBeFalse();
    }

    [Test]
    public void Phase4_CommandLayouts_MatchCurrentGpuAbi()
    {
        int fullBytes = Marshal.SizeOf<GPUIndirectRenderCommand>();
        int hotBytes = Marshal.SizeOf<GPUIndirectRenderCommandHot>();

        fullBytes.ShouldBe(80);
        hotBytes.ShouldBe(80);

        int[] commandCounts = [1_000, 10_000, 100_000];
        foreach (int count in commandCounts)
        {
            long aosBytes = (long)count * fullBytes * 2L;
            long hotBytesTotal = (long)count * hotBytes * 2L;

            TestContext.WriteLine($"Phase4 bandwidth model count={count}: AoS={aosBytes} bytes Hot={hotBytesTotal} bytes");
            hotBytesTotal.ShouldBe(aosBytes);
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

