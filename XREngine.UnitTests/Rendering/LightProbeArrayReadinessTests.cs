using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class LightProbeArrayReadinessTests
{
    [Test]
    public void LightProbeReadiness_RequiresGeneratedIblTextures()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.Properties.cs");
        string iblSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.IBL.cs");

        source.ShouldContain("public bool HasUsableIblTextures");
        source.ShouldContain("IrradianceTexture is not null");
        source.ShouldContain("PrefilterTexture is not null");
        // The probe is usable only when one generated IBL output is active.
        source.ShouldContain("&& TryGetActiveIblOutput(out _);");
        iblSource.ShouldContain("return generation is not null && IblTexturesValid && CaptureVersion != 0u;");
    }

    [Test]
    public void LightProbeIblOutputs_ResetReadinessUntilGenerated()
    {
        string iblSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.IBL.cs");
        string lifecycleSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.Lifecycle.cs");

        string resolve = SliceBetween(
            iblSource,
            "private void ResolvePendingIblOutputVersion()",
            "private void ScheduleIblRetryAfterRejectedWriter()");

        iblSource.ShouldContain("CompleteIblGenerationAttempt");
        iblSource.ShouldContain("bool irradiance = GenerateIrradianceInternal();");
        iblSource.ShouldContain("bool prefilter = GeneratePrefilterInternal();");
        iblSource.ShouldContain("return irradiance && prefilter;");
        iblSource.ShouldContain("pending.MarkOutputValid();");
        iblSource.ShouldContain("IblTexturesValid = false;");
        iblSource.ShouldContain("CaptureVersion = 0;");
        // Each generation writes new output textures. The textures become
        // active only after the writer completed and the output is valid.
        iblSource.ShouldContain("XRTexture2D irradiance = CreateIrradianceTexture(_pendingIrradianceExtent);");
        iblSource.ShouldContain("CreatePrefilterTexture(_pendingPrefilterExtent));");
        resolve.ShouldContain("if (!pending.IsWriterComplete())\n                return;");
        resolve.ShouldContain("if (!pending.OutputValid)");
        resolve.ShouldContain("IblTexturesValid = true;");
        resolve.ShouldContain("CaptureVersion = pending.Generation;");
        resolve.IndexOf("if (!pending.OutputValid)", StringComparison.Ordinal)
            .ShouldBeLessThan(resolve.IndexOf("IblTexturesValid = true;", StringComparison.Ordinal));
        lifecycleSource.Replace("\r\n", "\n").ShouldContain("else\n                    {\n                        IblTexturesValid = false;\n                        CaptureVersion = 0;\n                    }");
    }

    [Test]
    public void LightProbeIblPasses_MustPrepareAndReportFullscreenRenderSuccess()
    {
        string iblSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.IBL.cs");
        string quadSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Objects/Render Targets/XRQuadFrameBuffer.cs");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs");

        iblSource.ShouldContain("private static bool RunFullscreenProbePass");
        iblSource.ShouldContain("if (!TryPrepareProbePass(fbo.FullScreenMesh, fbo.Name))");
        iblSource.ShouldContain("if (mesh.TryPrepareForRendering(out string reason, forceNoStereo: true))");
        iblSource.ShouldContain("rendered = fbo.Render(null, true);");
        iblSource.ShouldContain("if (!RunFullscreenProbePass(mipTargets[mip], Math.Max(1, (int)target.Width >> mip), Math.Max(1, (int)target.Height >> mip)))");
        iblSource.ShouldContain("if (!RunFullscreenProbePass(mipTargets[mip], mipWidth, mipHeight))");
        quadSource.ShouldContain("public bool TryPrepareForRendering");
        quadSource.ShouldContain("public bool Render(XRFrameBuffer? target = null, bool forceNoStereo = true)");
        meshSource.ShouldContain("apiObject is IRenderPreparationState preparationState");
    }

    [Test]
    public void LightProbeIblRetry_RunsGpuWorkOnRenderThread()
    {
        string iblSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.IBL.cs");
        string componentSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.cs");
        string captureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/SceneCaptureComponent.cs");

        componentSource.ShouldContain("private bool _iblRetryQueuedOnRenderThread;");
        iblSource.ShouldContain("if (!RuntimeEngine.IsRenderThread || !IsIblRetryRendererActive())\n            {\n                QueueIblGenerationRetryOnActiveRenderer();\n                return;\n            }");
        iblSource.ShouldContain("RuntimeEngine.AddRenderThreadCoroutine(\n                () => ResumeIblGenerationRetryOnActiveRenderer(coroutineGeneration),");
        iblSource.ShouldContain("\"LightProbe.RetryPendingIblGeneration\"");
        captureSource.ShouldContain("\"SceneCapture.SyncCaptureTextureWrites\"");
        captureSource.ShouldContain("if (!RuntimeEngine.IsRenderThread)");
    }

    [Test]
    public void DefaultPipelines_OnlyBuildProbeArraysFromUsableProbeTextures()
    {
        string pipeline1 = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        string advancedPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs");

        pipeline1.ShouldContain("probe.HasUsableIblTextures");
        advancedPipeline.ShouldContain("probe.HasUsableIblTextures");
        pipeline1.ShouldNotContain("probe.IrradianceTexture != null && probe.PrefilterTexture != null");
        advancedPipeline.ShouldNotContain("probe.IrradianceTexture != null && probe.PrefilterTexture != null");
    }

    [Test]
    public void LightProbeGridSpawner_StartupSequentialCaptureRunsWhenGridBecomesReady()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeGridSpawnerComponent.cs");

        source.ShouldContain("RequestGridBuild(replaceExistingGrid: false, restartSequentialCapture: AutoSequentialCaptureOnBeginPlay);");
        source.ShouldContain("if (request.RestartSequentialCapture && _spawnedNodes.Count > 0)");
        source.ShouldContain("BeginSequentialCapture();");
    }

    [Test]
    public void AdvancedPipeline_DefersStructuralProbeArrayRebuildsDuringBatchCapture()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs");
        string lightsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.cs");

        source.ShouldContain("state.PendingProbeRefreshDeferredByBatchCapture");
        source.ShouldContain("state.ObservedLightProbeBatchCompletedVersion = batchCompletedVersion;");
        source.ShouldContain("bool batchCaptureActive = world.Lights.LightProbeBatchCaptureActive;");
        source.ShouldContain("LightProbeBatchCompletedVersion");
        source.ShouldContain("batchCompletedSinceLastSync");
        source.ShouldContain("ProbeConfigurationChanged(state, readyProbes, state.CachedReadyProbeSourceIndices, world, batchCaptureActive)");
        source.ShouldContain("ScheduleDeferredStructuralProbeRefreshForBatchCapture");
        source.ShouldContain("if (!(state.PendingProbeRefreshDeferredByBatchCapture && batchCaptureActive))");
        source.ShouldContain("AreProbeBindingResourcesMissing(state)");
        lightsSource.ShouldContain("public int LightProbeBatchCompletedVersion");
        lightsSource.ShouldContain("Interlocked.Increment(ref _lightProbeBatchCompletedVersion)");
    }

    private static string SliceBetween(string source, string startToken, string endToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Expected start token '{startToken}'.");
        int end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Expected end token '{endToken}'.");
        return source[start..end];
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
