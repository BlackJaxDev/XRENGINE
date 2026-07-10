using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCoreHardeningPhase521Tests
{
    [Test]
    public void XrEyes_ShareCriticalViewFamilyAndForbidSilentFallback()
    {
        RenderOutputRequest left = RenderOutputRequest.CreateDefault(
            EVrOutputViewKind.LeftEye,
            EFrameOutputKind.OpenXREyeSubmit,
            frameId: 17UL,
            sourceRateHz: 90.0f);
        RenderOutputRequest right = RenderOutputRequest.CreateDefault(
            EVrOutputViewKind.RightEye,
            EFrameOutputKind.OpenXREyeSubmit,
            frameId: 17UL,
            sourceRateHz: 90.0f);

        left.OutputId.ShouldNotBe(0UL);
        right.OutputId.ShouldNotBe(left.OutputId);
        left.ViewFamilyId.ShouldBe(right.ViewFamilyId);
        left.OutputClass.ShouldBe(ERenderOutputClass.XrCritical);
        left.Schedule.Priority.ShouldBe(ERenderOutputPriority.Critical);
        left.Schedule.HardDeadline.ShouldBeTrue();
        left.Schedule.DeadlineMs.ShouldBe(1000.0 / 90.0, 0.0001);
        left.Schedule.MaxContentAgeFrames.ShouldBe(0u);
        left.FallbackPolicy.ShouldBe(ERenderOutputFallbackPolicy.None);
        left.QualityRequirements.ShouldBe(ERenderOutputQualityRequirement.GpuAccelerated);
        left.CompletionRequirement.ShouldBe(ERenderOutputCompletionRequirement.GpuCompleteBeforeRuntimeRelease);
        left.Allows(ERenderOutputWorkDisposition.ReusedStale).ShouldBeFalse();
        left.Allows(ERenderOutputWorkDisposition.QualityReduced).ShouldBeFalse();
    }

    [Test]
    public void DefaultOutputClasses_EncodeTheRequiredSchedulingOrderAndPolicy()
    {
        RenderOutputRequest desktop = RenderOutputRequest.CreateDefault(
            EVrOutputViewKind.DesktopEditor,
            EFrameOutputKind.DesktopScene);
        RenderOutputRequest mirror = RenderOutputRequest.CreateDefault(
            EVrOutputViewKind.CyclopeanDesktop,
            EFrameOutputKind.DesktopMirror);
        RenderOutputRequest pickup = RenderOutputRequest.CreateDefault(
            EVrOutputViewKind.Debug,
            EFrameOutputKind.VrPickupMirror);
        RenderOutputRequest shadow = RenderOutputRequest.CreateDefault(
            EVrOutputViewKind.Debug,
            EFrameOutputKind.Shadow);
        RenderOutputRequest probe = RenderOutputRequest.CreateDefault(
            EVrOutputViewKind.Debug,
            EFrameOutputKind.LightProbeCapture);

        desktop.OutputClass.ShouldBe(ERenderOutputClass.InteractiveScene);
        desktop.Schedule.Priority.ShouldBe(ERenderOutputPriority.Interactive);
        mirror.OutputClass.ShouldBe(ERenderOutputClass.Presentation);
        mirror.Schedule.Priority.ShouldBe(ERenderOutputPriority.Interactive);
        (mirror.FallbackPolicy & ERenderOutputFallbackPolicy.AllowCompositionReuse)
            .ShouldNotBe(ERenderOutputFallbackPolicy.None);
        mirror.Allows(ERenderOutputWorkDisposition.ReusedStale).ShouldBeTrue();
        pickup.OutputClass.ShouldBe(ERenderOutputClass.VisibleMirror);
        pickup.Schedule.Priority.ShouldBe(ERenderOutputPriority.VisibleAuxiliary);
        shadow.OutputClass.ShouldBe(ERenderOutputClass.RequiredDependency);
        shadow.Schedule.Priority.ShouldBe(ERenderOutputPriority.RequiredDependency);
        shadow.FallbackPolicy.ShouldBe(ERenderOutputFallbackPolicy.None);
        probe.OutputClass.ShouldBe(ERenderOutputClass.BackgroundCapture);
        probe.Schedule.Priority.ShouldBe(ERenderOutputPriority.Background);
        probe.Allows(ERenderOutputWorkDisposition.Deferred).ShouldBeTrue();
    }

    [Test]
    public void TargetCompatibility_CoversGenerationExtentFormatSamplesViewMaskAndExternalSlot()
    {
        RenderOutputTargetDescriptor target = new(
            ERenderOutputTargetClass.RuntimeExternalImage,
            StableTargetId: 41UL,
            TargetGeneration: 3UL,
            DisplayWidth: 2448u,
            DisplayHeight: 2448u,
            InternalWidth: 1836u,
            InternalHeight: 1836u,
            FormatCompatibilityKey: 97UL,
            SampleCount: 4u,
            ViewMask: 3u,
            ExternalImageSlot: 1);

        target.CompatibilityKey.ShouldNotBe((target with { TargetGeneration = 4UL }).CompatibilityKey);
        target.CompatibilityKey.ShouldNotBe((target with { InternalWidth = 1835u }).CompatibilityKey);
        target.CompatibilityKey.ShouldNotBe((target with { FormatCompatibilityKey = 98UL }).CompatibilityKey);
        target.CompatibilityKey.ShouldNotBe((target with { SampleCount = 1u }).CompatibilityKey);
        target.CompatibilityKey.ShouldNotBe((target with { ViewMask = 1u }).CompatibilityKey);
        target.CompatibilityKey.ShouldNotBe((target with { ExternalImageSlot = 2 }).CompatibilityKey);
    }

    [Test]
    public void PacingDecision_PublishesACompleteOutputRequest()
    {
        FrameOutputPacingDecision decision = FrameOutputPacingDecision.Due(
            EVrOutputViewKind.DesktopEditor,
            EFrameOutputKind.EditorScenePanel,
            frameId: 9UL,
            configuredTargetRateHz: 30.0f,
            sourceRateHz: 60.0f);

        decision.Request.IsDefined.ShouldBeTrue();
        decision.Request.FrameId.ShouldBe(9UL);
        decision.Request.Schedule.DesiredRateHz.ShouldBe(30.0f);
        decision.Request.Target.TargetClass.ShouldBe(ERenderOutputTargetClass.OffscreenFramebuffer);
    }

    [Test]
    public void ProfilerAndHarness_ExposeAndEnforceTheMultiOutputContract()
    {
        string capture = ReadWorkspaceFile("XRENGINE/Engine/Engine.ProfileCapture.cs");
        string harness = ReadWorkspaceFile("Tools/Measure-GameLoopRenderPipeline.ps1");

        capture.ShouldContain("ProfileCaptureSchemaVersion = 4");
        capture.ShouldContain("frame_output_workload_identity_hash");
        capture.ShouldContain("frame_output_unapproved_policy_event_count");
        capture.ShouldContain("frame_output_submission_rejection_count");
        capture.ShouldContain("VulkanPrimaryCommandBufferReusePolicy");
        capture.ShouldContain("VulkanObsHookPolicy");
        capture.ShouldContain("VulkanSkipImGui");
        capture.ShouldContain("SceneSettingsHash");
        capture.ShouldContain("OutputInventory");

        harness.ShouldContain("Test-RenderStatsStability");
        harness.ShouldContain("vulkan_retired_image_view_count");
        harness.ShouldContain("vulkan_retired_buffer_memory_count");
        harness.ShouldContain("vulkan_retired_descriptor_set_count");
        harness.ShouldContain("vulkan_retired_command_buffer_count");
        harness.ShouldContain("vulkan_retired_query_pool_count");
        harness.ShouldContain("vulkan_retired_buffer_view_count");
        harness.ShouldContain("frame_output_global_in_flight_wait_count");
        harness.ShouldContain("VulkanCommandBufferCleanReuseRatio");
        harness.ShouldContain("CaptureWorkloadIdentityCount");
        harness.ShouldContain("Invalid Vulkan performance capture");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string path = Path.Combine(
            ResolveRepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{relativePath}'.");
        return File.ReadAllText(path).Replace("\r\n", "\n");
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
