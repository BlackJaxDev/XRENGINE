using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanDesktopPlanStabilityTests
{
    [Test]
    public void PlannerCompatibilityExcludesRotatingDesktopTargetFromPhysicalPlanIdentity()
    {
        string planner = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string recordingFingerprint = SliceBetween(
            planner,
            "private static ulong ComputeFrameOpContextRecordingFingerprint",
            "private static XRFrameBuffer? ResolveFrameOpOutputFrameBuffer");
        string plannerFingerprint = SliceBetween(
            planner,
            "private static ulong ComputeResourcePlanCompatibilityFingerprint",
            "private static ResourcePlannerSignatureBreakdown ComputeResourcePlannerSignatureBreakdown");

        planner.ShouldContain("ResolveResourcePlanOutputTargetIdentity(context)");
        recordingFingerprint.ShouldContain("hash.Add(context.OutputTargetIdentity);");
        plannerFingerprint.ShouldContain("hash.Add(ResolveResourcePlanOutputTargetIdentity(context));");
        plannerFingerprint.ShouldNotContain("context.RecordingFingerprint");
        plannerFingerprint.ShouldNotContain("context.OutputTargetName");
    }

    [Test]
    public void ConditionalShadowRegistriesRemainStableAndOutputScoped()
    {
        string planner = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs");
        string merge = SliceBetween(
            planner,
            "private RenderResourceRegistry? BuildMergedFrameOpRegistry",
            "private static RenderResourceRegistry[] CollectUniqueFrameOpRegistries");
        string lookup = SliceBetween(
            planner,
            "private bool TryGetCachedMergedFrameOpRegistry",
            "private static int IndexOfFrameOpRegistryCacheSource");

        merge.ShouldContain("FrameOpPlannerStateKey ownerKey = BuildFrameOpPlannerStateKey(primaryContext);");
        merge.ShouldContain("retain its descriptors until the compatibility key changes");
        lookup.ShouldContain("!entry.OwnerKey.Equals(ownerKey)");
        lookup.ShouldContain("FrameOpRegistryCacheSource[] accumulatedSources = entry.Sources;");
        lookup.ShouldContain("AddRegistryDescriptors(persistentMerged, source, overwrite: false);");
        state.ShouldContain("public FrameOpPlannerStateKey OwnerKey { get; } = ownerKey;");
    }

    [Test]
    public void PlanReplacementUsesDeferredRetirementWithoutGlobalDrain()
    {
        string planner = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string commit = SliceBetween(
            planner,
            "private void CommitPhysicalAllocatorPlan",
            "private VulkanPhysicalImageGroup? PreserveAutoExposureHistory");

        commit.ShouldContain("oldAllocator.TryRetirePhysicalResources");
        commit.ShouldContain("_lastResourcePlanReplacementRetiredImageCount");
        commit.ShouldNotContain("WaitForAllInFlightWork");
        commit.ShouldNotContain("DeviceWaitIdle");
        commit.ShouldNotContain("ForceFlush");
        planner.ShouldNotContain("ForceFlushAllRetiredResourcesAfterWaiting(\"ResourcePlanReplacement\")");
        planner.ShouldNotContain("TryPreReleaseActiveImagesForOpenXrMirrorTransition");
    }

    [Test]
    public void RejectedDesktopFramePublishesOnlyKnownGoodPriorContent()
    {
        string frameLoop = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");
        string swapchain = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs");
        string recovery = SliceBetween(
            frameLoop,
            "bool TryPresentAbortedDirtyFrame",
            "stageStartTimestamp = Stopwatch.GetTimestamp();");

        recovery.ShouldContain("if (!imageWasEverPresented)");
        recovery.ShouldContain("if (!imageHasValidPresentedContent)");
        recovery.ShouldContain("Refusing skipped-frame present for swapchain image");
        recovery.ShouldContain("lastReplacementAllocation");
        recovery.ShouldContain("exposureHistoryRetained");
        recovery.ShouldContain("Re-presented previously completed content");
        swapchain.ShouldContain("_swapchainImageHasValidPresentedContent = new bool[imageCount];");
        swapchain.ShouldContain("_swapchainImageHasValidPresentedContent = null;");
    }

    private static string SliceBetween(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Missing method start '{startMarker}'.");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Missing method end '{endMarker}'.");
        return source[start..end];
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

        throw new DirectoryNotFoundException("Could not locate the XRENGINE repository root.");
    }
}
