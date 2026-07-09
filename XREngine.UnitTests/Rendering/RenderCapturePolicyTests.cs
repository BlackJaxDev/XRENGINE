using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderCapturePolicyTests
{
    [Test]
    public void CapturePresets_CoverEverySupportedCaptureConsumer()
    {
        RenderCapturePolicy.GenericSceneCapture.Kind.ShouldBe(ERenderCaptureKind.SceneCapture);
        RenderCapturePolicy.LightProbe.Kind.ShouldBe(ERenderCaptureKind.LightProbe);
        RenderCapturePolicy.ReflectionProbe.Kind.ShouldBe(ERenderCaptureKind.ReflectionProbe);
        RenderCapturePolicy.GiProbe.Kind.ShouldBe(ERenderCaptureKind.GiProbe);
        RenderCapturePolicy.ThumbnailOrUiPreview.Kind.ShouldBe(ERenderCaptureKind.ThumbnailOrUiPreview);
        RenderCapturePolicy.DiagnosticFbo.Kind.ShouldBe(ERenderCaptureKind.DiagnosticFbo);
    }

    [Test]
    public void LightProbePolicy_UsesMinimalHdrDirectFboPath()
    {
        RenderCapturePolicy policy = RenderCapturePolicy.LightProbe;

        policy.UsesMinimalDirectFboPath.ShouldBeTrue();
        policy.OutputHDR.ShouldBeTrue();
        policy.RenderShadows.ShouldBeTrue();
        policy.Allows(ERenderCapturePass.PreRender).ShouldBeTrue();
        policy.Allows(ERenderCapturePass.Background).ShouldBeTrue();
        policy.Allows(ERenderCapturePass.OpaqueDeferred).ShouldBeTrue();
        policy.Allows(ERenderCapturePass.OpaqueForward).ShouldBeTrue();
        policy.Allows(ERenderCapturePass.Masked).ShouldBeTrue();
        policy.Allows(ERenderCapturePass.Transparent).ShouldBeTrue();
        policy.Allows(ERenderCapturePass.ComputeLighting).ShouldBeFalse();
        policy.Allows(ERenderCapturePass.DebugOverlays).ShouldBeFalse();
        policy.AllowTemporalHistory.ShouldBeFalse();
        policy.AllowAutoExposure.ShouldBeFalse();
        policy.AllowBloom.ShouldBeFalse();
        policy.AllowTemporalAntiAliasing.ShouldBeFalse();
        policy.AllowVendorUpscale.ShouldBeFalse();
        policy.AllowViewportFinalOutput.ShouldBeFalse();
    }

    [Test]
    public void SpecializedPolicies_ExposeTheirIntentionalPassDifferences()
    {
        RenderCapturePolicy.GiProbe.RenderTransparent.ShouldBeFalse();
        RenderCapturePolicy.ThumbnailOrUiPreview.RenderScreenSpaceUI.ShouldBeTrue();
        RenderCapturePolicy.DiagnosticFbo.RenderDebugOverlays.ShouldBeTrue();
        RenderCapturePolicy.DiagnosticFbo.RenderShadows.ShouldBeFalse();
    }

    [Test]
    public void SceneCaptureViewport_UsesPolicyInsteadOfDirectFboMutation()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/SceneCaptureComponent.cs");

        source.ShouldContain("viewport.ApplyCapturePolicy(CaptureRenderPolicy);");
        source.ShouldContain("RenderCapturePolicy.GenericSceneCapture");
        source.ShouldNotContain("UseDirectFboTargetCommandsForCapture");
        source.ShouldNotContain("viewport.UseDirectFboTargetCommandsWhenRenderingToFbo =");
    }

    [Test]
    public void DefaultPipelines_GateTheSameDirectCapturePasses()
    {
        string pipeline1 = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs");
        string pipeline2 = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default2/DefaultRenderPipeline2.CommandChain.cs");

        foreach (ERenderCapturePass pass in Enum.GetValues<ERenderCapturePass>())
        {
            string policyGate = $"ERenderCapturePass.{pass}";
            pipeline1.ShouldContain(policyGate);
            pipeline2.ShouldContain(policyGate);
        }
    }

    [Test]
    public void MinimalCaptureProfile_DoesNotDescribeViewportManagedResources()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.Resources.cs");

        source.ShouldContain("MinimalDirectCapture = 1UL << 16");
        source.ShouldContain("viewport?.CapturePolicy.UsesMinimalDirectFboPath == true");
        source.ShouldContain("DefaultPipelineResourceFeature.MinimalDirectCapture) != 0");

        int captureGuard = source.IndexOf("DefaultPipelineResourceFeature.MinimalDirectCapture) != 0", StringComparison.Ordinal);
        int coreResources = source.IndexOf("DeclareCoreTextures(builder);", StringComparison.Ordinal);
        captureGuard.ShouldBeGreaterThanOrEqualTo(0);
        coreResources.ShouldBeGreaterThan(captureGuard);
    }

    [Test]
    public void LightProbeIblGeneration_RemainsExplicitAndPostCapture()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.IBL.cs");

        source.ShouldContain("protected override RenderCapturePolicy CaptureRenderPolicy");
        source.ShouldContain("RenderCapturePolicy.LightProbe");
        source.ShouldContain("RunFullscreenProbePass");
        source.ShouldContain("base.FinalizeCubemapCapture();");
        source.ShouldContain("CompleteIblGenerationAttempt(releaseTransientEnvironmentTexturesOnSuccess: true);");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            string candidate = Path.Combine(directory, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException($"Could not locate workspace file '{relativePath}'.");
    }
}
