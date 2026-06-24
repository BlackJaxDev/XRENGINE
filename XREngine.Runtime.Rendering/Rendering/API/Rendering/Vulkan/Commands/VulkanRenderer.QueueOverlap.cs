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

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private VulkanBarrierPlanner.QueueOwnershipConfig BuildQueueOwnershipConfig(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        QueueFamilyIndices familyIndices = FamilyQueueIndices;
        uint graphicsFamily = familyIndices.GraphicsFamilyIndex ?? 0u;
        uint candidateComputeFamily = familyIndices.ComputeFamilyIndex ?? graphicsFamily;
        uint candidateTransferFamily = familyIndices.TransferFamilyIndex ?? candidateComputeFamily;

        EVulkanGpuDrivenProfile profile = VulkanFeatureProfile.ActiveProfile;
        QueueOverlapMetrics metrics = CaptureQueueOverlapMetrics(passMetadata);

        bool promotedMode;
        bool demotedMode;
        EVulkanQueueOverlapMode overlapMode = ResolveQueueOverlapMode(profile, metrics, out promotedMode, out demotedMode);

        bool useComputeOwnership =
            overlapMode is EVulkanQueueOverlapMode.GraphicsCompute or EVulkanQueueOverlapMode.GraphicsComputeTransfer &&
            candidateComputeFamily != graphicsFamily &&
            metrics.ComputePassCount >= 2;

        bool useTransferOwnership =
            overlapMode == EVulkanQueueOverlapMode.GraphicsComputeTransfer &&
            candidateTransferFamily != graphicsFamily &&
            candidateTransferFamily != candidateComputeFamily &&
            metrics.TransferUsageCount >= 4;

        uint computeFamily = useComputeOwnership ? candidateComputeFamily : graphicsFamily;
        uint transferFamily = useTransferOwnership ? candidateTransferFamily : computeFamily;

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanQueueOverlapWindow(
            metrics.OverlapCandidatePassCount,
            metrics.TransferCost,
            metrics.FrameDelta,
            promotedMode,
            demotedMode);

        _lastResolvedQueueOverlapMode = overlapMode;

        Debug.VulkanEvery(
            "Vulkan.QueueOwnership.Policy",
            TimeSpan.FromSeconds(2),
            "Queue ownership policy: profile={0} mode={1} gfx={2} compute={3} transfer={4} useCompute={5} useTransfer={6} computePasses={7} overlapCandidates={8} transferUsages={9} transferCost={10} qTransfers={11} stageFlushes={12} frameDeltaMs={13:F3}",
            profile,
            overlapMode,
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

        return new VulkanBarrierPlanner.QueueOwnershipConfig(
            graphicsFamily,
            computeFamily,
            transferFamily);
    }

    private QueueOverlapMetrics CaptureQueueOverlapMetrics(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        bool hasMetadata = passMetadata is { Count: > 0 };
        int computePassCount = hasMetadata ? passMetadata!.Count(static p => p.Stage == ERenderGraphPassStage.Compute) : 0;
        int transferUsageCount = hasMetadata
            ? passMetadata!.Sum(static p => p.ResourceUsages.Count(static u => u.ResourceType is ERenderPassResourceType.TransferSource or ERenderPassResourceType.TransferDestination))
            : 0;
        int overlapCandidatePassCount = hasMetadata
            ? passMetadata!.Count(IsQueueOverlapCandidatePass)
            : 0;

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

        return new QueueOverlapMetrics(
            computePassCount,
            transferUsageCount,
            overlapCandidatePassCount,
            transferCost,
            queueOwnershipTransfers,
            stageFlushes,
            frameDelta);
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

    private EVulkanQueueOverlapMode ResolveQueueOverlapMode(EVulkanGpuDrivenProfile profile, in QueueOverlapMetrics metrics, out bool promotedMode, out bool demotedMode)
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
