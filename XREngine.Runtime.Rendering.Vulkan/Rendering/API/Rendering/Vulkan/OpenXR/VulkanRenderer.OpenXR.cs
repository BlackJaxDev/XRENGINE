using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int OpenXrEyeResourcePlannerStateCount = 2;
    private const uint OpenXrExternalSwapchainTargetImageIndex = 0;
    private const string OpenXrLeftExternalSwapchainTargetName = "<openxr-left-swapchain>";
    private const string OpenXrRightExternalSwapchainTargetName = "<openxr-right-swapchain>";
    private const string OpenXrExternalSwapchainTargetName = "<openxr-swapchain>";
    private const ulong MinDesktopFramesBeforeOpenXrRuntimeSessionStart = 4;
    private const double OpenXrVulkanAllocatorPressureDeferRatio = 0.9;
    private const long OpenXrVulkanAllocatorPressureReserveBytes = 512L * 1024L * 1024L;
    private const double OpenXrVulkanImageAllocationPressurePreflightRatio = 0.84;
    private const long OpenXrVulkanImageAllocationPressureReserveBytes = 768L * 1024L * 1024L;
    private const double OpenXrVulkanImageAllocationCountPreflightRatio = 0.80;
    private const int OpenXrVulkanImageAllocationCountReserve = 768;
    private static readonly TimeSpan OpenXrRuntimeSessionStartDirtyQuietPeriod = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OpenXrRuntimeSessionStartDirtyMaxWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OpenXrRuntimeSessionStartPendingFrameMaxWait = TimeSpan.FromSeconds(2);
    private static bool TraceOpenXrStereoBlits =>
        XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw ||
        XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw;

    private Dictionary<ulong, PrimaryCommandArtifactOwner> OpenXrPrimaryCommandArtifactOwners =>
        OutputRuntime.OpenXrBackend.GetPrimaryCommandArtifactOwners<PrimaryCommandArtifactOwner>();
    private Dictionary<VulkanOpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState> OpenXrResourcePlannerStates =>
        OutputRuntime.OpenXrBackend.GetResourcePlannerStates<VulkanOpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState>();

    internal static bool IsOpenXrStrictSpsFaultBoundary(
        EOpenXrStrictSpsFaultInjectionStage requested,
        EOpenXrStrictSpsFaultInjectionStage boundary)
        => requested != EOpenXrStrictSpsFaultInjectionStage.None && requested == boundary;

    internal static bool ShouldFreeTemporaryOpenXrCommandBuffer(
        EVulkanQueueSubmissionDisposition disposition)
        => disposition != EVulkanQueueSubmissionDisposition.SubmittedIncomplete;

    private void ReleaseCurrentThreadOpenXrCaches()
    {
        OutputRuntime.OpenXrBackend.CurrentThreadExecutionState.Reset();
    }

    public override bool IsRenderingExternalSwapchainTarget => IsThreadOpenXrExternalSwapchainTarget;
    internal bool IsPrewarmingOpenXrExternalSwapchainTarget =>
        IsThreadOpenXrExternalSwapchainTarget &&
        Volatile.Read(ref OutputRuntime.OpenXrBackend.ExternalSwapchainPrewarmDepth) > 0;
    public override bool AllowSynchronousResourceUploads
        => !IsThreadSynchronousResourceUploadBlocked &&
           Volatile.Read(ref OutputRuntime.OpenXrBackend.SynchronousResourceUploadBlockDepth) == 0;

    private bool IsThreadOpenXrExternalSwapchainTarget =>
        OutputRuntime.OpenXrBackend.CurrentThreadExecutionState.ExternalSwapchainDepth > 0;

    private bool IsThreadSynchronousResourceUploadBlocked =>
        OutputRuntime.OpenXrBackend.CurrentThreadExecutionState.SynchronousUploadBlockDepth > 0;

    internal IDisposable BlockSynchronousResourceUploads(string reason)
    {
        return new SynchronousResourceUploadBlockScope(this, reason);
    }

    private void LogSynchronousResourceUploadBlock(string reason)
    {
        if (OpenXrVulkanTraceEnabled || DescriptorTraceEnabled)
            Debug.VulkanWarningEvery(
                $"Vulkan.SyncUploads.Blocked.{reason}.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[VulkanDescriptor] syncUploads=blocked reason={0} depth={1}",
                reason,
                Math.Max(
                    OutputRuntime.OpenXrBackend.CurrentThreadExecutionState.SynchronousUploadBlockDepth,
                    Volatile.Read(ref OutputRuntime.OpenXrBackend.SynchronousResourceUploadBlockDepth)));
    }

    private void ReserveOpenXrFrameDataSlotsIfRequired(string reason)
    {
        if (!ShouldReserveOpenXrFrameDataSlots())
            return;

        int frameDataSlotCount = ResolveOpenXrFrameDataSlotCount(OutputRuntime.Desktop.Images?.Length ?? 0);
        EnsureOpenXrFrameDataSlotCapacity(frameDataSlotCount);
        bool grew = EnsureDescriptorFrameSlotFrameCountFloor(frameDataSlotCount);
        if (grew || OpenXrVulkanTraceEnabled)
        {
            Debug.Vulkan(
                "[OpenXR] Reserved Vulkan frame-data slots for OpenXR. Reason={0} desktopSwapchainImages={1} frameDataSlots={2} descriptorFrameSlots={3}",
                reason,
                OutputRuntime.Desktop.Images?.Length ?? 0,
                frameDataSlotCount,
                DescriptorFrameSlotFrameCount);
        }
    }

    private static bool ShouldReserveOpenXrFrameDataSlots()
        => RuntimeEngine.GameSettings?.VRRuntime == EVRRuntime.OpenXR ||
           RuntimeEngine.VRState.IsOpenXRActive ||
           IsUnitTestingOpenXrLaunchMode() ||
           IsTruthyEnvironmentValue(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestUseOpenXr));

    private static bool IsUnitTestingOpenXrLaunchMode()
    {
        string? unitTestVrMode = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestVrMode);
        return string.Equals(unitTestVrMode, "MonadoOpenXR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(unitTestVrMode, "OpenXR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTruthyEnvironmentValue(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));

    internal void MarkOpenXrPrimaryCommandArtifactOwnersDirty()
    {
        lock (OutputRuntime.OpenXrBackend.PrimaryCommandArtifactOwnersLock)
        {
            foreach (PrimaryCommandArtifactOwner owner in OpenXrPrimaryCommandArtifactOwners.Values)
                owner.Dirty = true;
        }
    }

    private void MarkUnsubmittedOpenXrPrimaryCommandBufferDirty(
        in OpenXrRecordedEyeCommandBuffer recorded,
        string reason)
    {
        if (!recorded.OwnedByOpenXrPrimaryCache || recorded.CommandBuffer.Handle == 0)
            return;

        lock (OutputRuntime.OpenXrBackend.PrimaryCommandArtifactOwnersLock)
        {
            foreach (PrimaryCommandArtifactOwner owner in OpenXrPrimaryCommandArtifactOwners.Values)
            {
                if (owner.PrimaryCommandBuffer.Handle != recorded.CommandBuffer.Handle)
                    continue;

                owner.Dirty = true;
                owner.DirtyReason = reason;
                return;
            }
        }
    }

    public override bool TryGetExternalSwapchainTargetRegion(out BoundingRectangle region)
    {
        VulkanOpenXrThreadExecutionState executionState =
            OutputRuntime.OpenXrBackend.CurrentThreadExecutionState;
        if (IsThreadOpenXrExternalSwapchainTarget &&
            executionState.FrameContext.TargetRegion.Width > 0 &&
            executionState.FrameContext.TargetRegion.Height > 0)
        {
            region = executionState.FrameContext.TargetRegion;
            return true;
        }

        region = default;
        return false;
    }

    private bool TryGetExternalSwapchainTargetIdentity(out int targetIdentity, out string? targetName)
    {
        VulkanOpenXrThreadExecutionState executionState =
            OutputRuntime.OpenXrBackend.CurrentThreadExecutionState;
        if (IsThreadOpenXrExternalSwapchainTarget &&
            executionState.FrameContext.TargetIdentity != 0)
        {
            targetIdentity = executionState.FrameContext.TargetIdentity;
            targetName = executionState.FrameContext.TargetName;
            return true;
        }

        targetIdentity = 0;
        targetName = null;
        return false;
    }

    internal IDisposable EnterOpenXrExternalSwapchainRenderScope(
        uint width,
        uint height,
        int targetIdentity = 0,
        string? targetName = null,
        EVulkanFrameOpContextKind contextKind = EVulkanFrameOpContextKind.OpenXrEye)
    {
        if (width == 0 || height == 0)
            throw new InvalidOperationException("OpenXR external swapchain render scope requires a non-zero target extent.");

        if (width > int.MaxValue || height > int.MaxValue)
            throw new InvalidOperationException($"OpenXR external swapchain extent {width}x{height} exceeds supported render-region dimensions.");

        VulkanOpenXrFrameContext frameContext = new(
            ResourcePlannerStateIndex: -1,
            ViewIndex: 0,
            ImageIndex: OpenXrExternalSwapchainTargetImageIndex,
            new Extent2D(width, height),
            targetIdentity,
            targetName,
            contextKind);
        return EnterOpenXrExternalSwapchainRenderScope(in frameContext);
    }

    private IDisposable EnterOpenXrExternalSwapchainRenderScope(
        in VulkanOpenXrFrameContext frameContext)
    {
        if (!frameContext.HasExternalTarget)
            throw new InvalidOperationException("OpenXR external swapchain render scope requires a non-zero target extent.");

        return new OpenXrExternalSwapchainRenderScope(this, in frameContext);
    }

    internal VulkanOpenXrDiagnosticsSnapshot CaptureOpenXrDiagnostics()
        => OutputRuntime.OpenXrBackend.CaptureDiagnostics<
            PrimaryCommandArtifactOwner,
            VulkanOpenXrViewResourcePlannerContextKey,
            ResourcePlannerRuntimeState>();

    /// <summary>
    /// Identifies the allocator-owned render plan for an OpenXR view family. The acquired runtime
    /// image is deliberately excluded: runtime image handles, image-view handles, and frame-slot
    /// identity belong to command-buffer and submission variants, while the engine-owned intermediate
    /// resources remain compatible as the runtime rotates swapchain images.
    /// </summary>
    internal static int BuildOpenXrExternalSwapchainPlannerTargetIdentity(uint openXrViewIndex, ulong viewBatchStructuralIdentity = 0UL)
    {
        unchecked
        {
            int hash = 0x4F585254;
            hash = (hash * 397) ^ (int)openXrViewIndex;
            hash = (hash * 397) ^ 0x53494E54;
            hash = (hash * 397) ^ unchecked((int)viewBatchStructuralIdentity);
            hash = (hash * 397) ^ unchecked((int)(viewBatchStructuralIdentity >> 32));
            return hash == 0 ? 1 : hash;
        }
    }

    private static uint ResolveOpenXrExternalSwapchainViewIndex(int resourcePlannerStateIndex)
        => resourcePlannerStateIndex <= 0 ? 0u : (uint)resourcePlannerStateIndex;

    private static string ResolveOpenXrExternalSwapchainTargetName(uint openXrViewIndex)
        => openXrViewIndex switch
        {
            0u => OpenXrLeftExternalSwapchainTargetName,
            1u => OpenXrRightExternalSwapchainTargetName,
            _ => OpenXrExternalSwapchainTargetName,
        };

    private static VulkanOpenXrFrameContext CreateOpenXrEyeFrameContext(
        in OpenXrEyeSwapchainRenderRequest request)
        => new(
            request.ResourcePlannerStateIndex,
            request.OpenXrViewIndex,
            request.OpenXrImageIndex,
            request.Extent,
            BuildOpenXrExternalSwapchainPlannerTargetIdentity(
                request.OpenXrViewIndex,
                request.ViewBatchStructuralIdentity),
            ResolveOpenXrExternalSwapchainTargetName(request.OpenXrViewIndex),
            EVulkanFrameOpContextKind.OpenXrEye);

    private static VulkanOpenXrFrameContext CreateOpenXrMirrorFrameContext(
        in OpenXrEyeMirrorRenderRequest request)
        => new(
            request.ResourcePlannerStateIndex,
            request.OpenXrViewIndex,
            request.OpenXrImageIndex,
            request.Extent,
            BuildOpenXrExternalSwapchainPlannerTargetIdentity(
                request.OpenXrViewIndex,
                request.ViewBatchStructuralIdentity),
            ResolveOpenXrExternalSwapchainTargetName(request.OpenXrViewIndex),
            EVulkanFrameOpContextKind.OpenXrMirror);

}
