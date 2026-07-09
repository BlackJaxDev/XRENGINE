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

        source.ShouldContain("viewport.Camera.OutputHDROverride = true;");
        source.ShouldContain("viewport.Camera.AntiAliasingModeOverride = EAntiAliasingMode.None;");
        source.ShouldContain("viewport.RenderPipeline ??= RuntimeEngine.Rendering.NewRenderPipeline();");
        source.ShouldContain("viewport.SetRenderPipelineFromCamera = false;");
    }

    [Test]
    public void LightProbeComponent_RendersFullPrefilterMipChain()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.IBL.cs");

        source.ShouldContain("int maxMipLevels = PrefilterTexture.SmallestMipmapLevel + 1;");
        source.ShouldContain("for (int mip = 0; mip < maxMipLevels; ++mip)");
        source.ShouldContain("float roughness = maxMipLevels <= 1 ? 0.0f : (float)mip / (maxMipLevels - 1);");
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
