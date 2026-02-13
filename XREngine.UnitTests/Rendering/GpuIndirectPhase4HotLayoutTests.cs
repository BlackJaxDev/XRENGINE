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
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.Core.cs");

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
    public void Phase4_CullingHotPath_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs");

        source.ShouldContain("private void BuildSourceHotCommandBuffer(GPUScene scene, uint inputCount)");
        source.ShouldContain("_buildHotCommandsProgram.Uniform(\"InputCount\", (int)inputCount);");
        source.ShouldContain("_cullingComputeShader.Uniform(\"UseHotCommands\", useHotCommands ? 1 : 0);");
        source.ShouldContain("_cullingComputeShader.BindBuffer(_sourceHotCommandBuffer!, 9);");
        source.ShouldContain("_cullingComputeShader.BindBuffer(_culledHotCommandBuffer!, 10);");
        source.ShouldContain("_bvhFrustumCullProgram.Uniform(\"UseHotCommands\", useHotCommands ? 1u : 0u);");
        source.ShouldContain("_bvhFrustumCullProgram.BindBuffer(_sourceHotCommandBuffer!, 9);");
        source.ShouldContain("_bvhFrustumCullProgram.BindBuffer(_culledHotCommandBuffer!, 10);");
        source.ShouldContain("_extractSoAComputeShader.Uniform(\"UseHotCommands\", useHotCommands ? 1 : 0);");
        source.ShouldContain("_extractSoAComputeShader.BindBuffer(_sourceHotCommandBuffer!, 3);");
        source.ShouldContain("ShouldExtractSoAForCurrentPolicy");
        source.ShouldContain("Engine.EffectiveSettings.GpuCullingDataLayout");
        source.ShouldContain("ShippingFast profile requires hot-command layout for frustum culling.");
        source.ShouldContain("ShippingFast profile requires hot-command layout for BVH culling.");
    }

    [Test]
    public void Phase4_OcclusionAndIndirectHotPath_SourceContracts_ArePresent()
    {
        string occlusionSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.Occlusion.cs");
        string indirectSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs");

        occlusionSource.ShouldContain("_hiZOcclusionProgram.Uniform(\"UseHotCommands\", useHotCommands ? 1 : 0);");
        occlusionSource.ShouldContain("_hiZOcclusionProgram.BindBuffer(_culledHotCommandBuffer!, 9);");
        occlusionSource.ShouldContain("_hiZOcclusionProgram.BindBuffer(_occlusionCulledHotBuffer!, 10);");
        occlusionSource.ShouldContain("(_culledHotCommandBuffer, _occlusionCulledHotBuffer) = (_occlusionCulledHotBuffer, _culledHotCommandBuffer);");

        indirectSource.ShouldContain("_indirectRenderTaskShader.Uniform(\"UseHotCommands\", _culledCommandsUseHotLayout ? 1 : 0);");
        indirectSource.ShouldContain("_culledHotCommandBuffer?.BindTo(_indirectRenderTaskShader!, 9);");
        indirectSource.ShouldContain("_buildHotCommandsProgram.Uniform(\"InputCount\", (int)inputCount);");
        indirectSource.ShouldContain("ShippingFast requires hot command layout", Case.Insensitive);
    }

    [Test]
    public void Phase4_ColdPayloadMigration_SourceContracts_ArePresent()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPUIndirectRenderCommand.cs");

        source.ShouldContain("public struct GPUIndirectRenderCommandCold");
        source.ShouldContain("public GPUIndirectRenderCommandCold ToCold()");
        source.ShouldContain("public static GPUIndirectRenderCommand FromHotCold");
    }

    [Test]
    public void Phase4_OverflowTailHandling_SourceContracts_ArePresent()
    {
        string hybridSource = ReadWorkspaceFile("XRENGINE/Rendering/HybridRenderingManager.cs");
        string passSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs");
        string sceneSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPUScene.cs");
        string settingsSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs");

        hybridSource.ShouldContain("private static bool TryReadDrawCount(XRDataBuffer? parameterBuffer, out uint drawCount)");
        hybridSource.ShouldContain("private static void ClearIndirectTail(XRDataBuffer indirectDrawBuffer, uint drawCount, uint maxCommands)");
        hybridSource.ShouldContain("if (!DebugSettings.SkipIndirectTailClear && drawCount < maxCommands)");

        passSource.ShouldContain("Overflow growth policy requested capacity increase");
        passSource.ShouldContain("scene.EnsureCommandCapacity(requestedCapacity)");

        sceneSource.ShouldContain("public uint EnsureCommandCapacity(uint requiredCapacity)");
        settingsSource.ShouldContain("public EGpuCullingDataLayout GpuCullingDataLayout");
    }

    [Test]
    public void Phase4_ShaderHotLayoutContracts_ArePresent()
    {
        string buildHot = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/GPURenderBuildHotCommands.comp");
        string culling = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/GPURenderCulling.comp");
        string occlusion = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/GPURenderOcclusionHiZ.comp");
        string bvh = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_frustum_cull.comp");
        string extractSoA = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/GPURenderExtractSoA.comp");

        buildHot.ShouldContain("uniform int InputCount;");
        buildHot.ShouldContain("const uint HOT_UINTS = 16u;");

        culling.ShouldContain("layout(std430, binding = 9) readonly buffer InputHotCommandsBuffer");
        culling.ShouldContain("layout(std430, binding = 10) writeonly buffer CulledHotCommandsBuffer");
        culling.ShouldContain("uniform int UseHotCommands;");

        occlusion.ShouldContain("layout(std430, binding = 9) readonly buffer InputHotCommandsBuffer");
        occlusion.ShouldContain("layout(std430, binding = 10) writeonly buffer OutputHotCommandsBuffer");
        occlusion.ShouldContain("uniform int UseHotCommands;");

        bvh.ShouldContain("layout(std430, binding = 9) readonly buffer InputHotCommandsBuffer");
        bvh.ShouldContain("layout(std430, binding = 10) writeonly buffer CulledHotCommandsBuffer");
        bvh.ShouldContain("uniform uint UseHotCommands;");

        extractSoA.ShouldContain("layout(std430, binding = 3) readonly buffer InHotCommands");
        extractSoA.ShouldContain("uniform int UseHotCommands;");
    }

    [Test]
    public void Phase4_BandwidthModel_BenchmarkRepresentativeCommandCounts()
    {
        int fullBytes = Marshal.SizeOf<GPUIndirectRenderCommand>();
        int hotBytes = Marshal.SizeOf<GPUIndirectRenderCommandHot>();

        fullBytes.ShouldBe(192);
        hotBytes.ShouldBe(64);

        int[] commandCounts = [1_000, 10_000, 100_000];
        foreach (int count in commandCounts)
        {
            long aosBytes = (long)count * fullBytes * 2L;
            long hotBytesTotal = (long)count * hotBytes * 2L;

            TestContext.WriteLine($"Phase4 bandwidth model count={count}: AoS={aosBytes} bytes Hot={hotBytesTotal} bytes");
            hotBytesTotal.ShouldBeLessThan(aosBytes);
        }
    }

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

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }
}
