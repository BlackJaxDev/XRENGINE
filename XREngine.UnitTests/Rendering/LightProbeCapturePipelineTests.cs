using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class LightProbeCapturePipelineTests
{
    [Test]
    public void SceneCaptureComponent_ReusesOneSharedCaptureViewportAcrossProbeFaces()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/SceneCaptureComponent.cs");

        source.ShouldContain("private static XRViewport? s_sharedCaptureViewport;");
        source.ShouldContain("XRViewport sharedViewport = GetOrCreateSharedCaptureViewport();");
        source.ShouldContain("Viewports[0] = sharedViewport;");
        source.ShouldContain("captureTransform.SetWorldTranslationRotation(translation, rotation);");
        source.ShouldNotContain("XRCubeFrameBuffer.GetCamerasPerFace(0.1f, 10000.0f, true, Transform);");
    }

    [Test]
    public void SceneCaptureComponent_SynchronizesCaptureWritesWithRendererMemoryBarrier()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/SceneCaptureComponent.cs");

        source.ShouldContain("const EMemoryBarrierMask captureTextureBarrier");
        source.ShouldContain("EMemoryBarrierMask.Framebuffer |");
        source.ShouldContain("EMemoryBarrierMask.TextureFetch |");
        source.ShouldContain("EMemoryBarrierMask.TextureUpdate;");
        source.ShouldContain("AbstractRenderer.Current?.MemoryBarrier(captureTextureBarrier);");
        source.ShouldContain("() => AbstractRenderer.Current?.MemoryBarrier(captureTextureBarrier)");
        source.ShouldNotContain("WaitForGpu");
        source.ShouldNotContain("OpenGLRenderer");
    }

    [Test]
    public void LightProbeComponent_ConfiguresTheSharedCaptureViewportForProbeRendering()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.IBL.cs");
        string captureProfile = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/SceneCaptureComponent.AdvancedProfile.cs");
        string sharedCapture = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/SceneCaptureComponent.cs");

        source.ShouldContain("viewport.ApplyCapturePolicy(CaptureRenderPolicy);");
        source.ShouldContain("=> RenderCapturePolicy.LightProbe;");
        source.ShouldContain("=> RenderPipelineOffscreenIntent.ReflectionProbe();");
        source.ShouldContain("RuntimeEngine.Rendering.NewOffscreenCaptureRenderPipeline()");
        source.ShouldContain("viewport.SetRenderPipelineFromCamera = false;");

        // The shared capture viewport owns the pipeline request for every probe face.
        sharedCapture.ShouldContain("ConfigureCapturePipeline(viewport);");
        captureProfile.ShouldContain(": RenderPipelineRequest.OffscreenCapture();");
        captureProfile.ShouldContain("viewport.PipelineRequest = request;");
        captureProfile.ShouldContain("viewport.RenderPipeline = RuntimeEngine.Rendering.NewRenderPipeline(viewport.PipelineRequest);");
    }

    [Test]
    public void LightProbeComponent_RendersFullPrefilterMipChain()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.IBL.cs");
        string targets = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.ConvolutionTargets.cs");

        source.ShouldContain("XRQuadFrameBuffer[] mipTargets = GetIblMipTargets(prefilter: true, target);");
        source.ShouldContain("int maxMipLevels = mipTargets.Length;");
        source.ShouldContain("for (int mip = 0; mip < maxMipLevels; ++mip)");

        // Each prefilter mip target keeps its own roughness for the full mip chain.
        targets.ShouldContain("int count = output.SmallestMipmapLevel + 1;");
        targets.ShouldContain("for (int mip = 0; mip < count; mip++)");
        targets.ShouldContain("material.SetFloat(0, count <= 1 ? 0.0f : (float)mip / (count - 1));");
        targets.ShouldContain("targets[mip].SetRenderTargets((output, EFrameBufferAttachment.ColorAttachment0, mip, -1));");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");
        return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
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
