using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class LightProbeCapturePipelineTests
{
    [Test]
    public void LightProbeComponent_UsesDedicatedCapturePipeline()
    {
        string source = ReadWorkspaceFile("XRENGINE/Scene/Components/Capture/LightProbeComponent.cs");

        source.ShouldContain("ConfigureCaptureRenderPipelines();");
        source.ShouldContain("viewport.RenderPipeline = new LightProbeRenderPipeline();");
        source.ShouldContain("viewport.SetRenderPipelineFromCamera = false;");
    }

    [Test]
    public void LightProbeRenderPipeline_StaysLightweightAndSkipsPostProcessStages()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Types/LightProbeRenderPipeline.cs");

        source.ShouldContain("public sealed class LightProbeRenderPipeline : RenderPipeline");
        source.ShouldContain("OverrideProtected = true;");
        source.ShouldContain("VPRC_ForwardPlusLightCullingPass");
        source.ShouldContain("EDefaultRenderPass.OpaqueDeferred");
        source.ShouldContain("EDefaultRenderPass.TransparentForward");

        source.ShouldNotContain("VPRC_TemporalAccumulationPass");
        source.ShouldNotContain("AmbientOcclusion");
        source.ShouldNotContain("Bloom");
        source.ShouldNotContain("MotionBlur");
        source.ShouldNotContain("DepthOfField");
        source.ShouldNotContain("VPRC_RenderScreenSpaceUI");
        source.ShouldNotContain("VPRC_RenderDebugShapes");
        source.ShouldNotContain("VPRC_RenderDebugPhysics");
    }

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