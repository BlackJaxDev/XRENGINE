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
    public void PlannerAllocatorKey_TracksDescriptorOnlyGenerationChanges()
    {
        FrameOpContext first = CreateContext(
            EVulkanFrameOpContextKind.MainViewport,
            descriptorGeneration: 10);
        FrameOpContext descriptorOnlyChange = first with { DescriptorGeneration = 11 };
        FrameOpContext allocationChange = first with { ResourceGeneration = 2 };

        VulkanFramePlanner.BuildFrameOpPlannerStateKey(first)
            .ShouldNotBe(VulkanFramePlanner.BuildFrameOpPlannerStateKey(descriptorOnlyChange));
        VulkanFramePlanner.BuildFrameOpPlannerStateKey(first)
            .ShouldNotBe(VulkanFramePlanner.BuildFrameOpPlannerStateKey(allocationChange));
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

        FrameOpContext first = CreateContext(
            EVulkanFrameOpContextKind.MainViewport,
            descriptorGeneration: 10) with { ResourceRegistry = firstRegistry };
        FrameOpContext second = first with { ResourceRegistry = secondRegistry };

        VulkanFramePlanner.BuildFrameOpPlannerStateKey(first)
            .ShouldNotBe(VulkanFramePlanner.BuildFrameOpPlannerStateKey(second));
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

        FrameOpContext captured = CreateContext(
            EVulkanFrameOpContextKind.MainViewport,
            descriptorGeneration: 10) with
        {
            ResourceRegistry = firstRegistry,
            ResourceRegistrySignatureSnapshot = firstRegistry.DescriptorSignature,
        };
        FrameOpContext referenceChangedAfterCapture = captured with
        {
            ResourceRegistry = secondRegistry,
        };
        FrameOpContext recaptured = referenceChangedAfterCapture with
        {
            ResourceRegistrySignatureSnapshot = secondRegistry.DescriptorSignature,
        };

        VulkanFramePlanner.BuildFrameOpPlannerStateKey(captured)
            .ShouldBe(VulkanFramePlanner.BuildFrameOpPlannerStateKey(referenceChangedAfterCapture));
        VulkanFramePlanner.BuildFrameOpPlannerStateKey(captured)
            .ShouldNotBe(VulkanFramePlanner.BuildFrameOpPlannerStateKey(recaptured));
    }

    [Test]
    public void MainViewportPlannerKey_IgnoresRotatingTargetSlotButPreservesOutputOwnership()
    {
        FrameOpContext desktop = CreateContext(
            EVulkanFrameOpContextKind.MainViewport,
            descriptorGeneration: 10) with
        {
            OutputFrameBufferIdentity = 700,
            OutputTargetIdentity = 1,
            OutputTargetName = "DesktopSwapchain[0]",
        };
        FrameOpContext rotatedTarget = desktop with
        {
            OutputTargetIdentity = 2,
            OutputTargetName = "DesktopSwapchain[1]",
            RecordingFingerprint = desktop.RecordingFingerprint + 1,
        };
        FrameOpContext anotherViewport = rotatedTarget with
        {
            ViewportIdentity = desktop.ViewportIdentity + 1,
        };
        FrameOpContext captureTarget = desktop with
        {
            ContextKind = EVulkanFrameOpContextKind.SceneCapture,
        };
        FrameOpContext rotatedCaptureTarget = captureTarget with
        {
            OutputTargetIdentity = captureTarget.OutputTargetIdentity + 1,
        };

        VulkanFramePlanner.BuildFrameOpPlannerStateKey(desktop)
            .ShouldBe(VulkanFramePlanner.BuildFrameOpPlannerStateKey(rotatedTarget));
        VulkanFramePlanner.BuildFrameOpPlannerStateKey(desktop)
            .ShouldNotBe(VulkanFramePlanner.BuildFrameOpPlannerStateKey(anotherViewport));
        VulkanFramePlanner.BuildFrameOpPlannerStateKey(captureTarget)
            .ShouldNotBe(VulkanFramePlanner.BuildFrameOpPlannerStateKey(rotatedCaptureTarget));
    }

    [Test]
    public void InteractiveResizePlannerExtents_IsolateDownscaledMainViewportFromOneToOneUiPreview()
    {
        VulkanInteractiveResizePlannerExtentCache cache = new(capacity: 4);
        FrameOpContext main = CreateContext(
            EVulkanFrameOpContextKind.MainViewport,
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
        FrameOpContext uiPreview = CreateContext(
            EVulkanFrameOpContextKind.UiPreview,
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
            VulkanFramePlanner.BuildInteractiveResizePlannerContextKey(main);
        VulkanInteractiveResizePlannerContextKey uiPreviewKey =
            VulkanFramePlanner.BuildInteractiveResizePlannerContextKey(uiPreview);
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

        FrameOpContext rotatedMainTarget = main with { OutputTargetIdentity = 999 };
        VulkanFramePlanner.BuildInteractiveResizePlannerContextKey(rotatedMainTarget).ShouldBe(mainKey);

        cache.Clear();
        cache.Count.ShouldBe(0);
        cache.GetOrCapture(mainKey, resizedMainCandidate, out capturedMain, out _).ShouldBe(resizedMainCandidate);
        capturedMain.ShouldBeTrue();
    }

    [Test]
    public void InteractiveResizePlannerExtentCache_OverflowPreservesExistingSnapshotsAndReportsOnce()
    {
        VulkanInteractiveResizePlannerExtentCache cache = new(capacity: 2);
        FrameOpContext main = CreateContext(
            EVulkanFrameOpContextKind.MainViewport,
            descriptorGeneration: 1) with
        {
            PipelineIdentity = 701,
            ViewportIdentity = 801,
            OutputFrameBufferIdentity = 901,
        };
        FrameOpContext uiPreview = CreateContext(
            EVulkanFrameOpContextKind.UiPreview,
            descriptorGeneration: 1) with
        {
            PipelineIdentity = 702,
            ViewportIdentity = 802,
            OutputFrameBufferIdentity = 902,
        };
        FrameOpContext diagnostic = CreateContext(
            EVulkanFrameOpContextKind.DiagnosticCapture,
            descriptorGeneration: 1) with
        {
            PipelineIdentity = 703,
            ViewportIdentity = 803,
            OutputFrameBufferIdentity = 903,
        };
        VulkanInteractiveResizePlannerContextKey mainKey =
            VulkanFramePlanner.BuildInteractiveResizePlannerContextKey(main);
        VulkanInteractiveResizePlannerContextKey uiPreviewKey =
            VulkanFramePlanner.BuildInteractiveResizePlannerContextKey(uiPreview);
        VulkanInteractiveResizePlannerContextKey diagnosticKey =
            VulkanFramePlanner.BuildInteractiveResizePlannerContextKey(diagnostic);
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
        FrameOpContext[] contexts =
        [
            CreateContext(EVulkanFrameOpContextKind.MainViewport, 1),
            CreateContext(EVulkanFrameOpContextKind.SceneCapture, 2),
            CreateContext(EVulkanFrameOpContextKind.LightProbeCapture, 3),
            CreateContext(EVulkanFrameOpContextKind.OpenXrEye, 4),
            CreateContext(EVulkanFrameOpContextKind.OpenXrMirror, 5),
        ];

        Dictionary<VulkanFrameOpPlannerStateKey, VulkanResourceAllocator> owners = [];
        foreach (FrameOpContext context in contexts)
            owners[VulkanFramePlanner.BuildFrameOpPlannerStateKey(context)] = new VulkanResourceAllocator();

        owners.Count.ShouldBe(contexts.Length);
        owners.Values.Select(static allocator => allocator.OwnershipId).Distinct().Count().ShouldBe(contexts.Length);

        VulkanFrameOpPlannerStateKey captureKey = VulkanFramePlanner.BuildFrameOpPlannerStateKey(contexts[1]);
        VulkanResourceAllocator retiredCapture = owners[captureKey];
        owners.Remove(captureKey).ShouldBeTrue();
        retiredCapture.TryRetirePhysicalResources(null!).ShouldBeTrue();
        retiredCapture.TryRetirePhysicalResources(null!).ShouldBeFalse();
        retiredCapture.IsRetired.ShouldBeTrue();
        owners.Values.ShouldAllBe(static allocator => !allocator.IsRetired);
    }

    [Test]
    public void DescriptorOnlyChange_UsesDistinctOwnerAndPruningAnotherOwnerDoesNotRetireIt()
    {
        FrameOpContext main = CreateContext(
            EVulkanFrameOpContextKind.MainViewport,
            descriptorGeneration: 20);
        FrameOpContext descriptorChange = main with { DescriptorGeneration = 21 };
        FrameOpContext capture = CreateContext(
            EVulkanFrameOpContextKind.SceneCapture,
            descriptorGeneration: 1);

        Dictionary<VulkanFrameOpPlannerStateKey, VulkanResourceAllocator> owners = [];
        VulkanFrameOpPlannerStateKey mainKey = VulkanFramePlanner.BuildFrameOpPlannerStateKey(main);
        VulkanResourceAllocator mainOwner = owners.GetValueOrDefault(mainKey) ?? new VulkanResourceAllocator();
        owners[mainKey] = mainOwner;
        VulkanResourceAllocator descriptorChangeOwner = owners.GetValueOrDefault(
            VulkanFramePlanner.BuildFrameOpPlannerStateKey(descriptorChange)) ?? new VulkanResourceAllocator();
        VulkanResourceAllocator captureOwner = new();
        owners[VulkanFramePlanner.BuildFrameOpPlannerStateKey(capture)] = captureOwner;

        descriptorChangeOwner.ShouldNotBeSameAs(mainOwner);
        captureOwner.TryRetirePhysicalResources(null!).ShouldBeTrue();
        mainOwner.IsRetired.ShouldBeFalse();
        descriptorChangeOwner.IsRetired.ShouldBeFalse();
    }

    [Test]
    public void OpenXrPlannerPurpose_PreventsEyeMirrorPublishAndPrewarmCollisions()
    {
        VulkanOpenXrViewResourcePlannerContextKey[] keys =
            Enum.GetValues<EVulkanOpenXrResourcePlannerPurpose>()
                .Select(static purpose => new VulkanOpenXrViewResourcePlannerContextKey(
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
        state.State.ShouldBe(EVulkanDeviceState.CollectingFaultData);
        state.IsOperational.ShouldBeFalse();

        object queueGate = new();
        VulkanFrameTelemetry telemetry = new();
        using (VulkanQueueOperationLease lease = VulkanQueueOperationLease.TryEnter(queueGate, state, telemetry))
            lease.Acquired.ShouldBeFalse();

        state.CompleteLossCollection();
        state.State.ShouldBe(EVulkanDeviceState.Quiesced);
        state.Dispose();
        state.State.ShouldBe(EVulkanDeviceState.Disposed);

        state.TryBeginLossCollection().ShouldBeFalse();
        state.CompleteLossCollection();
        state.State.ShouldBe(EVulkanDeviceState.Disposed);
    }

    [Test]
    public void QueueOperationLease_SerializesConcurrentOperations()
    {
        VulkanDeviceStateMachine state = new();
        object queueGate = new();
        VulkanFrameTelemetry telemetry = new();
        int active = 0;
        int maxActive = 0;
        int acquired = 0;

        Parallel.For(0, 64, _ =>
        {
            using VulkanQueueOperationLease lease = VulkanQueueOperationLease.TryEnter(queueGate, state, telemetry);
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
    public void CommandDiagnostics_RecordAtTheNumericOpcodeBoundary()
    {
        string recorder = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "private int RecordTypedPrimaryOperation",
            "EVulkanPrimaryPlanNodeKind operationCode");

        recorder.ShouldContain("RecordVulkanCommandDiagnosticMarker(state.CommandBuffer, header.OpCode, resolvedPass, index)");
        recorder.ShouldContain("EVulkanPrimaryPlanNodeKind operationCode");
        recorder.ShouldContain("The dense recorder has no authoring operation instance.");
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

    private static FrameOpContext CreateContext(
        EVulkanFrameOpContextKind kind,
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
