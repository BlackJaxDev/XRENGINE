using System.Collections.Concurrent;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCoreHardeningPhase21Tests
{
    [Test]
    public void PlannerAllocatorKey_IgnoresDescriptorOnlyGenerationChanges()
    {
        VulkanRenderer.FrameOpContext first = CreateContext(
            VulkanRenderer.EVulkanFrameOpContextKind.MainViewport,
            descriptorGeneration: 10);
        VulkanRenderer.FrameOpContext descriptorOnlyChange = first with { DescriptorGeneration = 11 };
        VulkanRenderer.FrameOpContext allocationChange = first with { ResourceGeneration = 2 };

        VulkanRenderer.BuildFrameOpPlannerStateKey(first)
            .ShouldBe(VulkanRenderer.BuildFrameOpPlannerStateKey(descriptorOnlyChange));
        VulkanRenderer.BuildFrameOpPlannerStateKey(first)
            .ShouldNotBe(VulkanRenderer.BuildFrameOpPlannerStateKey(allocationChange));
    }

    [Test]
    public void PlannerAllocatorKey_SeparatesDistinctResourceRegistryContracts()
    {
        RenderResourceRegistry firstRegistry = new();
        firstRegistry.RegisterFrameBufferDescriptor(new FrameBufferResourceDescriptor(
            "PipelineOnly",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(32u, 32u),
            []));
        RenderResourceRegistry secondRegistry = new();
        secondRegistry.RegisterFrameBufferDescriptor(new FrameBufferResourceDescriptor(
            "PipelineAndOpenXrOutput",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(32u, 32u),
            []));

        VulkanRenderer.FrameOpContext first = CreateContext(
            VulkanRenderer.EVulkanFrameOpContextKind.MainViewport,
            descriptorGeneration: 10) with { ResourceRegistry = firstRegistry };
        VulkanRenderer.FrameOpContext second = first with { ResourceRegistry = secondRegistry };

        VulkanRenderer.BuildFrameOpPlannerStateKey(first)
            .ShouldNotBe(VulkanRenderer.BuildFrameOpPlannerStateKey(second));
    }

    [Test]
    public void PlannerAllocatorKey_UsesCapturedRegistrySignatureInsteadOfMutableRegistryReference()
    {
        RenderResourceRegistry firstRegistry = new();
        firstRegistry.RegisterFrameBufferDescriptor(new FrameBufferResourceDescriptor(
            "FirstGeneration",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(32u, 32u),
            []));
        RenderResourceRegistry secondRegistry = new();
        secondRegistry.RegisterFrameBufferDescriptor(new FrameBufferResourceDescriptor(
            "SecondGeneration",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(64u, 64u),
            []));

        VulkanRenderer.FrameOpContext captured = CreateContext(
            VulkanRenderer.EVulkanFrameOpContextKind.MainViewport,
            descriptorGeneration: 10) with
        {
            ResourceRegistry = firstRegistry,
            ResourceRegistrySignatureSnapshot = firstRegistry.DescriptorSignature,
        };
        VulkanRenderer.FrameOpContext referenceChangedAfterCapture = captured with
        {
            ResourceRegistry = secondRegistry,
        };
        VulkanRenderer.FrameOpContext recaptured = referenceChangedAfterCapture with
        {
            ResourceRegistrySignatureSnapshot = secondRegistry.DescriptorSignature,
        };

        VulkanRenderer.BuildFrameOpPlannerStateKey(captured)
            .ShouldBe(VulkanRenderer.BuildFrameOpPlannerStateKey(referenceChangedAfterCapture));
        VulkanRenderer.BuildFrameOpPlannerStateKey(captured)
            .ShouldNotBe(VulkanRenderer.BuildFrameOpPlannerStateKey(recaptured));
    }

    [Test]
    public void MainViewportPlannerKey_IgnoresRotatingTargetSlotButPreservesOutputOwnership()
    {
        VulkanRenderer.FrameOpContext desktop = CreateContext(
            VulkanRenderer.EVulkanFrameOpContextKind.MainViewport,
            descriptorGeneration: 10) with
        {
            OutputFrameBufferIdentity = 700,
            OutputTargetIdentity = 1,
            OutputTargetName = "DesktopSwapchain[0]",
        };
        VulkanRenderer.FrameOpContext rotatedTarget = desktop with
        {
            OutputTargetIdentity = 2,
            OutputTargetName = "DesktopSwapchain[1]",
            RecordingFingerprint = desktop.RecordingFingerprint + 1,
        };
        VulkanRenderer.FrameOpContext anotherViewport = rotatedTarget with
        {
            ViewportIdentity = desktop.ViewportIdentity + 1,
        };
        VulkanRenderer.FrameOpContext captureTarget = desktop with
        {
            ContextKind = VulkanRenderer.EVulkanFrameOpContextKind.SceneCapture,
        };
        VulkanRenderer.FrameOpContext rotatedCaptureTarget = captureTarget with
        {
            OutputTargetIdentity = captureTarget.OutputTargetIdentity + 1,
        };

        VulkanRenderer.BuildFrameOpPlannerStateKey(desktop)
            .ShouldBe(VulkanRenderer.BuildFrameOpPlannerStateKey(rotatedTarget));
        VulkanRenderer.BuildFrameOpPlannerStateKey(desktop)
            .ShouldNotBe(VulkanRenderer.BuildFrameOpPlannerStateKey(anotherViewport));
        VulkanRenderer.BuildFrameOpPlannerStateKey(captureTarget)
            .ShouldNotBe(VulkanRenderer.BuildFrameOpPlannerStateKey(rotatedCaptureTarget));
    }

    [Test]
    public void InteractiveResizePlannerExtents_IsolateDownscaledMainViewportFromOneToOneUiPreview()
    {
        VulkanInteractiveResizePlannerExtentCache cache = new(capacity: 4);
        VulkanRenderer.FrameOpContext main = CreateContext(
            VulkanRenderer.EVulkanFrameOpContextKind.MainViewport,
            descriptorGeneration: 10) with
        {
            PipelineIdentity = 301,
            ViewportIdentity = 401,
            OutputFrameBufferIdentity = 501,
            OutputTargetIdentity = 601,
            DisplayWidth = 1920,
            DisplayHeight = 1080,
            InternalWidth = 1280,
            InternalHeight = 720,
        };
        VulkanRenderer.FrameOpContext uiPreview = CreateContext(
            VulkanRenderer.EVulkanFrameOpContextKind.UiPreview,
            descriptorGeneration: 10) with
        {
            PipelineIdentity = 302,
            ViewportIdentity = 402,
            OutputFrameBufferIdentity = 502,
            OutputTargetIdentity = 602,
            DisplayWidth = 800,
            DisplayHeight = 600,
            InternalWidth = 800,
            InternalHeight = 600,
        };

        VulkanInteractiveResizePlannerContextKey mainKey =
            VulkanRenderer.BuildInteractiveResizePlannerContextKey(main);
        VulkanInteractiveResizePlannerContextKey uiPreviewKey =
            VulkanRenderer.BuildInteractiveResizePlannerContextKey(uiPreview);
        VulkanInteractiveResizePlannerExtentSnapshot mainSnapshot = new(
            main.DisplayWidth,
            main.DisplayHeight,
            main.InternalWidth,
            main.InternalHeight);
        VulkanInteractiveResizePlannerExtentSnapshot uiPreviewSnapshot = new(
            uiPreview.DisplayWidth,
            uiPreview.DisplayHeight,
            uiPreview.InternalWidth,
            uiPreview.InternalHeight);

        cache.GetOrCapture(mainKey, mainSnapshot, out bool capturedMain, out _).ShouldBe(mainSnapshot);
        cache.GetOrCapture(uiPreviewKey, uiPreviewSnapshot, out bool capturedUiPreview, out _).ShouldBe(uiPreviewSnapshot);

        capturedMain.ShouldBeTrue();
        capturedUiPreview.ShouldBeTrue();
        mainKey.ShouldNotBe(uiPreviewKey);
        cache.Count.ShouldBe(2);

        VulkanInteractiveResizePlannerExtentSnapshot resizedMainCandidate = new(1600, 900, 1067, 600);
        VulkanInteractiveResizePlannerExtentSnapshot resizedUiPreviewCandidate = new(720, 540, 720, 540);
        cache.GetOrCapture(mainKey, resizedMainCandidate, out capturedMain, out _).ShouldBe(mainSnapshot);
        cache.GetOrCapture(uiPreviewKey, resizedUiPreviewCandidate, out capturedUiPreview, out _).ShouldBe(uiPreviewSnapshot);

        capturedMain.ShouldBeFalse();
        capturedUiPreview.ShouldBeFalse();

        long allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_024; i++)
            cache.GetOrCapture(mainKey, resizedMainCandidate, out capturedMain, out _);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore;

        allocatedBytes.ShouldBe(0);
        capturedMain.ShouldBeFalse();

        VulkanRenderer.FrameOpContext rotatedMainTarget = main with { OutputTargetIdentity = 999 };
        VulkanRenderer.BuildInteractiveResizePlannerContextKey(rotatedMainTarget).ShouldBe(mainKey);

        cache.Clear();
        cache.Count.ShouldBe(0);
        cache.GetOrCapture(mainKey, resizedMainCandidate, out capturedMain, out _).ShouldBe(resizedMainCandidate);
        capturedMain.ShouldBeTrue();
    }

    [Test]
    public void InteractiveResizePlannerExtentCache_OverflowPreservesExistingSnapshotsAndReportsOnce()
    {
        VulkanInteractiveResizePlannerExtentCache cache = new(capacity: 2);
        VulkanRenderer.FrameOpContext main = CreateContext(
            VulkanRenderer.EVulkanFrameOpContextKind.MainViewport,
            descriptorGeneration: 1) with
        {
            PipelineIdentity = 701,
            ViewportIdentity = 801,
            OutputFrameBufferIdentity = 901,
        };
        VulkanRenderer.FrameOpContext uiPreview = CreateContext(
            VulkanRenderer.EVulkanFrameOpContextKind.UiPreview,
            descriptorGeneration: 1) with
        {
            PipelineIdentity = 702,
            ViewportIdentity = 802,
            OutputFrameBufferIdentity = 902,
        };
        VulkanRenderer.FrameOpContext diagnostic = CreateContext(
            VulkanRenderer.EVulkanFrameOpContextKind.DiagnosticCapture,
            descriptorGeneration: 1) with
        {
            PipelineIdentity = 703,
            ViewportIdentity = 803,
            OutputFrameBufferIdentity = 903,
        };
        VulkanInteractiveResizePlannerContextKey mainKey =
            VulkanRenderer.BuildInteractiveResizePlannerContextKey(main);
        VulkanInteractiveResizePlannerContextKey uiPreviewKey =
            VulkanRenderer.BuildInteractiveResizePlannerContextKey(uiPreview);
        VulkanInteractiveResizePlannerContextKey diagnosticKey =
            VulkanRenderer.BuildInteractiveResizePlannerContextKey(diagnostic);
        VulkanInteractiveResizePlannerExtentSnapshot mainSnapshot = new(1920, 1080, 1280, 720);
        VulkanInteractiveResizePlannerExtentSnapshot uiPreviewSnapshot = new(800, 600, 800, 600);
        VulkanInteractiveResizePlannerExtentSnapshot diagnosticCandidate = new(640, 360, 640, 360);

        cache.GetOrCapture(mainKey, mainSnapshot, out bool captured, out bool reportOverflow);
        captured.ShouldBeTrue();
        reportOverflow.ShouldBeFalse();
        cache.GetOrCapture(uiPreviewKey, uiPreviewSnapshot, out captured, out reportOverflow);
        captured.ShouldBeTrue();
        reportOverflow.ShouldBeFalse();

        cache.GetOrCapture(diagnosticKey, diagnosticCandidate, out captured, out reportOverflow)
            .ShouldBe(diagnosticCandidate);
        captured.ShouldBeFalse();
        reportOverflow.ShouldBeTrue();
        cache.Count.ShouldBe(2);

        VulkanInteractiveResizePlannerExtentSnapshot changedDiagnosticCandidate = new(320, 180, 320, 180);
        cache.GetOrCapture(diagnosticKey, changedDiagnosticCandidate, out captured, out reportOverflow)
            .ShouldBe(changedDiagnosticCandidate);
        captured.ShouldBeFalse();
        reportOverflow.ShouldBeFalse();

        VulkanInteractiveResizePlannerExtentSnapshot changedMainCandidate = new(1600, 900, 1067, 600);
        cache.GetOrCapture(mainKey, changedMainCandidate, out captured, out reportOverflow)
            .ShouldBe(mainSnapshot);
        captured.ShouldBeFalse();
        reportOverflow.ShouldBeFalse();

        cache.Clear();
        cache.GetOrCapture(diagnosticKey, changedDiagnosticCandidate, out captured, out reportOverflow)
            .ShouldBe(changedDiagnosticCandidate);
        captured.ShouldBeTrue();
        reportOverflow.ShouldBeFalse();
    }

    [Test]
    public void AlternatingPlannerContexts_RetainDistinctAllocatorOwners()
    {
        VulkanRenderer.FrameOpContext[] contexts =
        [
            CreateContext(VulkanRenderer.EVulkanFrameOpContextKind.MainViewport, 1),
            CreateContext(VulkanRenderer.EVulkanFrameOpContextKind.SceneCapture, 2),
            CreateContext(VulkanRenderer.EVulkanFrameOpContextKind.LightProbeCapture, 3),
            CreateContext(VulkanRenderer.EVulkanFrameOpContextKind.OpenXrEye, 4),
            CreateContext(VulkanRenderer.EVulkanFrameOpContextKind.OpenXrMirror, 5),
        ];

        Dictionary<VulkanRenderer.FrameOpPlannerStateKey, VulkanResourceAllocator> owners = [];
        foreach (VulkanRenderer.FrameOpContext context in contexts)
            owners[VulkanRenderer.BuildFrameOpPlannerStateKey(context)] = new VulkanResourceAllocator();

        owners.Count.ShouldBe(contexts.Length);
        owners.Values.Select(static allocator => allocator.OwnershipId).Distinct().Count().ShouldBe(contexts.Length);

        VulkanRenderer.FrameOpPlannerStateKey captureKey = VulkanRenderer.BuildFrameOpPlannerStateKey(contexts[1]);
        VulkanResourceAllocator retiredCapture = owners[captureKey];
        owners.Remove(captureKey).ShouldBeTrue();
        retiredCapture.TryRetirePhysicalResources(null!).ShouldBeTrue();
        retiredCapture.TryRetirePhysicalResources(null!).ShouldBeFalse();
        retiredCapture.IsRetired.ShouldBeTrue();
        owners.Values.ShouldAllBe(static allocator => !allocator.IsRetired);
    }

    [Test]
    public void DescriptorOnlyChange_ReusesOwnerAndPruningAnotherOwnerDoesNotRetireIt()
    {
        VulkanRenderer.FrameOpContext main = CreateContext(
            VulkanRenderer.EVulkanFrameOpContextKind.MainViewport,
            descriptorGeneration: 20);
        VulkanRenderer.FrameOpContext descriptorChange = main with { DescriptorGeneration = 21 };
        VulkanRenderer.FrameOpContext capture = CreateContext(
            VulkanRenderer.EVulkanFrameOpContextKind.SceneCapture,
            descriptorGeneration: 1);

        Dictionary<VulkanRenderer.FrameOpPlannerStateKey, VulkanResourceAllocator> owners = [];
        VulkanRenderer.FrameOpPlannerStateKey mainKey = VulkanRenderer.BuildFrameOpPlannerStateKey(main);
        VulkanResourceAllocator mainOwner = owners.GetValueOrDefault(mainKey) ?? new VulkanResourceAllocator();
        owners[mainKey] = mainOwner;
        VulkanResourceAllocator descriptorChangeOwner = owners.GetValueOrDefault(
            VulkanRenderer.BuildFrameOpPlannerStateKey(descriptorChange)) ?? new VulkanResourceAllocator();
        VulkanResourceAllocator captureOwner = new();
        owners[VulkanRenderer.BuildFrameOpPlannerStateKey(capture)] = captureOwner;

        descriptorChangeOwner.ShouldBeSameAs(mainOwner);
        captureOwner.TryRetirePhysicalResources(null!).ShouldBeTrue();
        mainOwner.IsRetired.ShouldBeFalse();
    }

    [Test]
    public void OpenXrPlannerPurpose_PreventsEyeMirrorPublishAndPrewarmCollisions()
    {
        VulkanRenderer.OpenXrViewResourcePlannerContextKey[] keys =
            Enum.GetValues<VulkanRenderer.EOpenXrResourcePlannerPurpose>()
                .Select(static purpose => new VulkanRenderer.OpenXrViewResourcePlannerContextKey(
                    purpose,
                    ResourcePlannerStateIndex: 0,
                    OpenXrViewIndex: 0,
                    OpenXrImageIndex: 0,
                    CommandChainImageKey: 0,
                    FrameDataSlotIndex: 0,
                    FoveationResourceKey: 0,
                    FoveationAttachmentKind: EVrFoveationAttachmentKind.None,
                    FoveationAttachmentOwnedByResourcePlanner: false))
                .ToArray();

        keys.Distinct().Count().ShouldBe(keys.Length);
    }

    [Test]
    public void DeviceStateMachine_FirstLossWriterWinsAndTerminalStatesRejectQueueLeases()
    {
        VulkanDeviceStateMachine state = new();
        int winners = 0;
        Parallel.For(0, 64, _ =>
        {
            if (state.TryBeginLossCollection())
                Interlocked.Increment(ref winners);
        });

        winners.ShouldBe(1);
        state.State.ShouldBe(VulkanRenderer.EVulkanDeviceState.CollectingFaultData);
        state.IsOperational.ShouldBeFalse();

        object queueGate = new();
        using (VulkanQueueOperationLease lease = VulkanQueueOperationLease.TryEnter(queueGate, state))
            lease.Acquired.ShouldBeFalse();

        state.CompleteLossCollection();
        state.State.ShouldBe(VulkanRenderer.EVulkanDeviceState.Quiesced);
        state.Dispose();
        state.State.ShouldBe(VulkanRenderer.EVulkanDeviceState.Disposed);
    }

    [Test]
    public void QueueOperationLease_SerializesConcurrentOperations()
    {
        VulkanDeviceStateMachine state = new();
        object queueGate = new();
        int active = 0;
        int maxActive = 0;
        int acquired = 0;

        Parallel.For(0, 64, _ =>
        {
            using VulkanQueueOperationLease lease = VulkanQueueOperationLease.TryEnter(queueGate, state);
            lease.Acquired.ShouldBeTrue();
            Interlocked.Increment(ref acquired);
            int nowActive = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maxActive, nowActive);
            Thread.SpinWait(20_000);
            Interlocked.Decrement(ref active);
        });

        acquired.ShouldBe(64);
        maxActive.ShouldBe(1);
    }

    [Test]
    public void CommandMarkerIdentity_RejectsAReusedHandleFromAnOlderRecording()
    {
        VulkanRenderer.CommandDiagnosticMarkerMatchesSubmittedCommand(
            markerHandle: 0x1234,
            markerGeneration: 8,
            submittedHandle: 0x1234,
            submittedGeneration: 9).ShouldBeFalse();

        VulkanRenderer.CommandDiagnosticMarkerMatchesSubmittedCommand(
            markerHandle: 0x1234,
            markerGeneration: 9,
            submittedHandle: 0x1234,
            submittedGeneration: 9).ShouldBeTrue();
    }

    [TestCase(-1, 64, 1024, 64)]
    [TestCase(0, 64, 1024, 64)]
    [TestCase(32, 64, 1024, 32)]
    [TestCase(4096, 64, 1024, 1024)]
    public void DeviceFaultCaps_ArePositiveAndHardBounded(
        int requested,
        int defaultValue,
        int maximumValue,
        int expected)
        => VulkanDiagnosticOptions.NormalizePositiveCap(requested, defaultValue, maximumValue).ShouldBe(expected);

    private static VulkanRenderer.FrameOpContext CreateContext(
        VulkanRenderer.EVulkanFrameOpContextKind kind,
        ulong descriptorGeneration)
        => new(
            PipelineIdentity: 100,
            ViewportIdentity: 200,
            PipelineInstance: null,
            ResourceRegistry: null,
            PassMetadata: null,
            DisplayWidth: 1024,
            DisplayHeight: 1024,
            InternalWidth: 1024,
            InternalHeight: 1024,
            OutputFrameBufferName: kind.ToString(),
            OutputTargetIdentity: (int)kind,
            OutputTargetName: kind.ToString(),
            OutputFrameBufferIdentity: (int)kind,
            ContextKind: kind,
            ContextId: (ulong)kind,
            RecordingFingerprint: (ulong)kind,
            ResourceGeneration: 1,
            DescriptorGeneration: descriptorGeneration);

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            int current = Volatile.Read(ref target);
            while (current < value)
            {
                int observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }
}
