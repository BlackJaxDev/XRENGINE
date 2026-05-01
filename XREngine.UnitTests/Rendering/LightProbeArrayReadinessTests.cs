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

        source.ShouldContain("public bool HasUsableIblTextures");
        source.ShouldContain("IrradianceTexture is not null");
        source.ShouldContain("PrefilterTexture is not null");
        source.ShouldContain("IblTexturesValid");
        source.ShouldContain("CaptureVersion > 0u");
    }

    [Test]
    public void LightProbeIblOutputs_ResetReadinessUntilGenerated()
    {
        string iblSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.IBL.cs");
        string lifecycleSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.Lifecycle.cs");

        iblSource.ShouldContain("CompleteIblGenerationAttempt");
        iblSource.ShouldContain("bool irradianceGenerated = GenerateIrradianceInternal();");
        iblSource.ShouldContain("bool prefilterGenerated = GeneratePrefilterInternal();");
        iblSource.ShouldContain("IblTexturesValid = false;");
        iblSource.ShouldContain("IblTexturesValid = success;");
        iblSource.ShouldContain("CaptureVersion++;");
        iblSource.ShouldContain("CaptureVersion = 0;");
        iblSource.ShouldContain("EnsureIrradianceOutputTexture");
        iblSource.ShouldContain("EnsurePrefilterOutputTexture");
        lifecycleSource.Replace("\r\n", "\n").ShouldContain("else\n                    {\n                        IblTexturesValid = false;\n                        CaptureVersion = 0;\n                    }");
    }

    [Test]
    public void LightProbeIblPasses_MustPrepareAndReportFullscreenRenderSuccess()
    {
        string iblSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.IBL.cs");
        string quadSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Objects/Render Targets/XRQuadFrameBuffer.cs");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs");

        iblSource.ShouldContain("private static bool RunFullscreenProbePass");
        iblSource.ShouldContain("if (!fbo.TryPrepareForRendering(forceNoStereo: true))");
        iblSource.ShouldContain("rendered = fbo.Render(null, true);");
        iblSource.ShouldContain("if (!RunFullscreenProbePass(_irradianceFBO, width, height))");
        iblSource.ShouldContain("if (!RunFullscreenProbePass(_prefilterFBO, mipWidth, mipHeight))");
        quadSource.ShouldContain("public bool TryPrepareForRendering");
        quadSource.ShouldContain("public bool Render(XRFrameBuffer? target = null, bool forceNoStereo = false)");
        meshSource.ShouldContain("apiObject is IRenderPreparationState preparationState");
    }

    [Test]
    public void DefaultPipelines_OnlyBuildProbeArraysFromUsableProbeTextures()
    {
        string pipeline1 = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.cs");
        string pipeline2 = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs");

        pipeline1.ShouldContain("probe.HasUsableIblTextures");
        pipeline2.ShouldContain("probe.HasUsableIblTextures");
        pipeline1.ShouldNotContain("probe.IrradianceTexture != null && probe.PrefilterTexture != null");
        pipeline2.ShouldNotContain("probe.IrradianceTexture != null && probe.PrefilterTexture != null");
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
    public void DefaultPipeline2_DefersStructuralProbeArrayRebuildsDuringBatchCapture()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs");
        string lightsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.cs");

        source.ShouldContain("_pendingProbeRefreshDeferredByBatchCapture");
        source.ShouldContain("_observedLightProbeBatchCompletedVersion");
        source.ShouldContain("bool batchCaptureActive = world.Lights.LightProbeBatchCaptureActive;");
        source.ShouldContain("LightProbeBatchCompletedVersion");
        source.ShouldContain("batchCompletedSinceLastSync");
        source.ShouldContain("ProbeConfigurationChanged(readyProbes, batchCaptureActive)");
        source.ShouldContain("ScheduleDeferredStructuralProbeRefreshForBatchCapture");
        source.ShouldContain("if (!(_pendingProbeRefreshDeferredByBatchCapture && batchCaptureActive))");
        source.ShouldContain("AreProbeBindingResourcesMissing()");
        lightsSource.ShouldContain("public int LightProbeBatchCompletedVersion");
        lightsSource.ShouldContain("Interlocked.Increment(ref _lightProbeBatchCompletedVersion)");
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
