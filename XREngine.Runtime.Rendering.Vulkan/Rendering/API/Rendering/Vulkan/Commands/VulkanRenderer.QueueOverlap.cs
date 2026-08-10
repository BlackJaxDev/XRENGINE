using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan.RenderGraph;

internal sealed partial class VulkanFramePlanner
{
    private ref EVulkanQueueOverlapMode _autoQueueOverlapMode => ref AutoQueueOverlapMode;
    private ref EVulkanQueueOverlapMode _lastResolvedQueueOverlapMode => ref LastResolvedQueueOverlapMode;
    private ref int _queueOverlapPromotionStabilityFrames => ref QueueOverlapPromotionStabilityFrames;
    private ref int _queueOverlapFramesInMode => ref QueueOverlapFramesInMode;
    private ref long _lastQueueOverlapSampleTimestamp => ref LastQueueOverlapSampleTimestamp;
    private ref ulong _lastQueueOverlapSampleFrameId => ref LastQueueOverlapSampleFrameId;
    private ref ulong _lastQueueOverlapPolicyFrameId => ref LastQueueOverlapPolicyFrameId;
    private ref double _queueOverlapFrameDeltaEmaMs => ref QueueOverlapFrameDeltaEmaMilliseconds;
    private ref double _queueOverlapModeStartFrameDeltaMs => ref QueueOverlapModeStartFrameDeltaMilliseconds;
    private ref ulong _queueOwnershipConfigCacheFrameId => ref QueueOwnershipConfigCacheFrameId;
    private List<VulkanQueueOwnershipConfigCacheEntry> _queueOwnershipConfigCache
        => MutableState.QueueOwnershipCache;

    internal VulkanBarrierPlanner.QueueOwnershipConfig BuildQueueOwnershipConfig(
        VulkanDeviceContext deviceContext,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        EVulkanGpuDrivenProfile profile)
    {
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (frameId != 0)
        {
            if (_queueOwnershipConfigCacheFrameId != frameId)
            {
                _queueOwnershipConfigCacheFrameId = frameId;
                _queueOwnershipConfigCache.Clear();
            }

            for (int index = 0; index < _queueOwnershipConfigCache.Count; index++)
            {
                VulkanQueueOwnershipConfigCacheEntry entry = _queueOwnershipConfigCache[index];
                if (ReferenceEquals(entry.PassMetadata, passMetadata))
                    return entry.Config;
            }
        }

        VulkanBarrierPlanner.QueueOwnershipConfig config = BuildQueueOwnershipConfigCore(
            deviceContext,
            passMetadata,
            frameId,
            profile);
        if (frameId != 0)
            _queueOwnershipConfigCache.Add(new VulkanQueueOwnershipConfigCacheEntry(passMetadata, config));

        return config;
    }

    private VulkanBarrierPlanner.QueueOwnershipConfig BuildQueueOwnershipConfigCore(
        VulkanDeviceContext deviceContext,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        ulong frameId,
        EVulkanGpuDrivenProfile profile)
    {
        QueueFamilyIndices familyIndices = deviceContext.QueueFamilies;
        uint graphicsFamily = familyIndices.GraphicsFamilyIndex ?? 0u;
        uint candidateComputeFamily = familyIndices.ComputeFamilyIndex ?? graphicsFamily;
        uint candidateTransferFamily = familyIndices.TransferFamilyIndex ?? candidateComputeFamily;

        VulkanQueueOverlapMetrics metrics = CaptureQueueOverlapMetrics(passMetadata);

        bool promotedMode;
        bool demotedMode;
        bool advanceAdaptivePolicy = frameId == 0 || _lastQueueOverlapPolicyFrameId != frameId;
        if (advanceAdaptivePolicy && frameId != 0)
            _lastQueueOverlapPolicyFrameId = frameId;
        EVulkanQueueOverlapMode requestedOverlapMode = ResolveQueueOverlapMode(
            profile,
            metrics,
            advanceAdaptivePolicy,
            out promotedMode,
            out demotedMode);

        // Frame-graph commands are still encoded into and submitted through one
        // graphics primary. Queue-schedule sidecars describe future work, but
        // they do not yet own native compute/transfer submissions. Publishing
        // distinct owner families here would therefore emit an acquire without
        // a source-queue release. Keep the executable barrier plan graphics-only
        // until the multi-queue executor supplies the paired command buffers,
        // semaphore edges, and ordered submissions.
        bool supportsFrameGraphMultiQueueSubmission =
            SupportsFrameGraphMultiQueueSubmission;
        bool useComputeOwnership =
            supportsFrameGraphMultiQueueSubmission &&
            requestedOverlapMode is EVulkanQueueOverlapMode.GraphicsCompute or EVulkanQueueOverlapMode.GraphicsComputeTransfer &&
            candidateComputeFamily != graphicsFamily &&
            metrics.ComputePassCount >= 2;

        bool useTransferOwnership =
            supportsFrameGraphMultiQueueSubmission &&
            requestedOverlapMode == EVulkanQueueOverlapMode.GraphicsComputeTransfer &&
            candidateTransferFamily != graphicsFamily &&
            candidateTransferFamily != candidateComputeFamily &&
            metrics.TransferUsageCount >= 4;

        uint computeFamily = useComputeOwnership ? candidateComputeFamily : graphicsFamily;
        uint transferFamily = useTransferOwnership ? candidateTransferFamily : computeFamily;

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanQueueOverlapWindow(
            metrics.OverlapCandidatePassCount,
            metrics.TransferCost,
            metrics.FrameDelta,
            supportsFrameGraphMultiQueueSubmission && promotedMode,
            supportsFrameGraphMultiQueueSubmission && demotedMode);

        _lastResolvedQueueOverlapMode = supportsFrameGraphMultiQueueSubmission
            ? requestedOverlapMode
            : EVulkanQueueOverlapMode.GraphicsOnly;

        if (XREnvironment.IsEnabled(XREngineEnvironmentVariables.VulkanRecordingDiag) ||
            XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw ||
            XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw)
        {
            Debug.VulkanEvery(
                "Vulkan.QueueOwnership.Policy",
                TimeSpan.FromSeconds(2),
                "Queue ownership policy: profile={0} requestedMode={1} executableMode={2} frameGraphMultiQueue={3} gfx={4} compute={5} transfer={6} useCompute={7} useTransfer={8} computePasses={9} overlapCandidates={10} transferUsages={11} transferCost={12} qTransfers={13} stageFlushes={14} frameDeltaMs={15:F3}",
                profile,
                requestedOverlapMode,
                _lastResolvedQueueOverlapMode,
                supportsFrameGraphMultiQueueSubmission,
                graphicsFamily,
                computeFamily,
                transferFamily,
                useComputeOwnership,
                useTransferOwnership,
                metrics.ComputePassCount,
                metrics.OverlapCandidatePassCount,
                metrics.TransferUsageCount,
                metrics.TransferCost,
                metrics.QueueOwnershipTransfers,
                metrics.BarrierStageFlushes,
                metrics.FrameDelta.TotalMilliseconds);
        }

        return new VulkanBarrierPlanner.QueueOwnershipConfig(
            graphicsFamily,
            computeFamily,
            transferFamily);
    }

    /// <summary>
    /// Gets whether the frame graph owns executable native submissions on
    /// non-graphics queues. Queue-schedule metadata alone does not satisfy this
    /// contract.
    /// </summary>
    private static bool SupportsFrameGraphMultiQueueSubmission => false;

    private VulkanQueueOverlapMetrics CaptureQueueOverlapMetrics(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        int computePassCount = 0;
        int transferUsageCount = 0;
        int overlapCandidatePassCount = 0;
        if (passMetadata is IReadOnlyList<RenderPassMetadata> passList)
        {
            for (int passIndex = 0; passIndex < passList.Count; passIndex++)
            {
                AccumulateQueueOverlapMetrics(
                    passList[passIndex],
                    ref computePassCount,
                    ref transferUsageCount,
                    ref overlapCandidatePassCount);
            }
        }
        else if (passMetadata is not null)
        {
            foreach (RenderPassMetadata pass in passMetadata)
            {
                AccumulateQueueOverlapMetrics(
                    pass,
                    ref computePassCount,
                    ref transferUsageCount,
                    ref overlapCandidatePassCount);
            }
        }

        int queueOwnershipTransfers = RuntimeEngine.Rendering.Stats.Vulkan.VulkanQueueOwnershipTransfers;
        int stageFlushes = RuntimeEngine.Rendering.Stats.Vulkan.VulkanBarrierStageFlushes;
        int transferCost = transferUsageCount + queueOwnershipTransfers + stageFlushes;

        TimeSpan frameDelta = TimeSpan.Zero;
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (_lastQueueOverlapSampleFrameId != frameId)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastQueueOverlapSampleTimestamp != 0)
            {
                long elapsedTicks = now - _lastQueueOverlapSampleTimestamp;
                if (elapsedTicks > 0)
                    frameDelta = TimeSpan.FromSeconds(elapsedTicks / (double)Stopwatch.Frequency);
            }

            _lastQueueOverlapSampleTimestamp = now;
            _lastQueueOverlapSampleFrameId = frameId;
        }

        return new VulkanQueueOverlapMetrics(
            computePassCount,
            transferUsageCount,
            overlapCandidatePassCount,
            transferCost,
            queueOwnershipTransfers,
            stageFlushes,
            frameDelta);
    }

    private static void AccumulateQueueOverlapMetrics(
        RenderPassMetadata pass,
        ref int computePassCount,
        ref int transferUsageCount,
        ref int overlapCandidatePassCount)
    {
        if (pass.Stage == ERenderGraphPassStage.Compute)
            computePassCount++;
        if (IsQueueOverlapCandidatePass(pass))
            overlapCandidatePassCount++;

        for (int usageIndex = 0; usageIndex < pass.ResourceUsages.Count; usageIndex++)
        {
            ERenderPassResourceType resourceType = pass.ResourceUsages[usageIndex].ResourceType;
            if (resourceType is ERenderPassResourceType.TransferSource or ERenderPassResourceType.TransferDestination)
                transferUsageCount++;
        }
    }

    private static bool IsQueueOverlapCandidatePass(RenderPassMetadata pass)
    {
        if (pass.Stage != ERenderGraphPassStage.Compute)
            return false;

        string name = pass.Name ?? string.Empty;
        return name.Contains("hiz", StringComparison.OrdinalIgnoreCase)
            || name.Contains("occlusion", StringComparison.OrdinalIgnoreCase)
            || name.Contains("indirect", StringComparison.OrdinalIgnoreCase);
    }

    private EVulkanQueueOverlapMode ResolveQueueOverlapMode(
        EVulkanGpuDrivenProfile profile,
        in VulkanQueueOverlapMetrics metrics,
        bool advanceAdaptivePolicy,
        out bool promotedMode,
        out bool demotedMode)
    {
        promotedMode = false;
        demotedMode = false;

        EVulkanQueueOverlapMode requestedMode = RuntimeEngine.EffectiveSettings.VulkanQueueOverlapMode;
        if (requestedMode != EVulkanQueueOverlapMode.Auto)
        {
            _autoQueueOverlapMode = requestedMode;
            _queueOverlapPromotionStabilityFrames = 0;
            _queueOverlapFramesInMode = 0;
            _queueOverlapModeStartFrameDeltaMs = -1.0;
            return requestedMode;
        }

        if (!advanceAdaptivePolicy)
            return _autoQueueOverlapMode;

        if (!VulkanFeatureProfile.IsActive)
        {
            _autoQueueOverlapMode = EVulkanQueueOverlapMode.GraphicsOnly;
            return _autoQueueOverlapMode;
        }

        bool hasFrameDelta = metrics.FrameDelta.Ticks > 0;
        if (hasFrameDelta)
        {
            double frameDeltaMs = metrics.FrameDelta.TotalMilliseconds;
            _queueOverlapFrameDeltaEmaMs = _queueOverlapFrameDeltaEmaMs < 0.0
                ? frameDeltaMs
                : (_queueOverlapFrameDeltaEmaMs * 0.85) + (frameDeltaMs * 0.15);
        }

        bool hasComputeCandidates = metrics.ComputePassCount >= 1;
        bool hasTransferCandidates = metrics.TransferUsageCount >= 2;

        EVulkanQueueOverlapMode desiredMode = profile switch
        {
            EVulkanGpuDrivenProfile.Diagnostics when hasComputeCandidates && hasTransferCandidates => EVulkanQueueOverlapMode.GraphicsComputeTransfer,
            EVulkanGpuDrivenProfile.Diagnostics when hasComputeCandidates => EVulkanQueueOverlapMode.GraphicsCompute,
            EVulkanGpuDrivenProfile.DevParity when hasComputeCandidates => EVulkanQueueOverlapMode.GraphicsCompute,
            _ => EVulkanQueueOverlapMode.GraphicsOnly,
        };

        _queueOverlapFramesInMode++;
        if (_queueOverlapModeStartFrameDeltaMs < 0.0 && hasFrameDelta)
            _queueOverlapModeStartFrameDeltaMs = metrics.FrameDelta.TotalMilliseconds;

        bool transferCostHealthy = metrics.TransferCost <= 1024;
        bool frameDeltaHealthy = _queueOverlapFrameDeltaEmaMs < 0.0 || _queueOverlapFrameDeltaEmaMs <= 40.0;

        if (desiredMode > _autoQueueOverlapMode && transferCostHealthy && frameDeltaHealthy)
        {
            _queueOverlapPromotionStabilityFrames++;
            int threshold = _autoQueueOverlapMode == EVulkanQueueOverlapMode.GraphicsOnly ? 8 : 16;
            if (_queueOverlapPromotionStabilityFrames >= threshold)
            {
                _autoQueueOverlapMode = _autoQueueOverlapMode == EVulkanQueueOverlapMode.GraphicsOnly
                    ? EVulkanQueueOverlapMode.GraphicsCompute
                    : EVulkanQueueOverlapMode.GraphicsComputeTransfer;

                _queueOverlapPromotionStabilityFrames = 0;
                _queueOverlapFramesInMode = 0;
                _queueOverlapModeStartFrameDeltaMs = _queueOverlapFrameDeltaEmaMs;
                promotedMode = true;
            }
        }
        else
        {
            _queueOverlapPromotionStabilityFrames = 0;
        }

        bool frameRegressed = hasFrameDelta && _queueOverlapModeStartFrameDeltaMs > 0.0 &&
            metrics.FrameDelta.TotalMilliseconds > _queueOverlapModeStartFrameDeltaMs * 1.15;
        bool queueCostTooHigh = metrics.QueueOwnershipTransfers > 256 || metrics.BarrierStageFlushes > 768;

        if (_autoQueueOverlapMode > EVulkanQueueOverlapMode.GraphicsOnly && _queueOverlapFramesInMode >= 12 && (frameRegressed || queueCostTooHigh))
        {
            _autoQueueOverlapMode = _autoQueueOverlapMode == EVulkanQueueOverlapMode.GraphicsComputeTransfer
                ? EVulkanQueueOverlapMode.GraphicsCompute
                : EVulkanQueueOverlapMode.GraphicsOnly;

            _queueOverlapPromotionStabilityFrames = 0;
            _queueOverlapFramesInMode = 0;
            _queueOverlapModeStartFrameDeltaMs = _queueOverlapFrameDeltaEmaMs;
            demotedMode = true;
        }

        return _autoQueueOverlapMode;
    }
}
