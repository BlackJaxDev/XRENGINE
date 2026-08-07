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
    private ref long _commandBufferDirtyGeneration => ref _commandRuntime.CommandBuffers.DirtyGeneration;
    private ref long _lastCommandBufferDirtyTimestamp => ref _commandRuntime.CommandBuffers.LastDirtyTimestamp;

    private long SnapshotCommandBufferDirtyGeneration()
        => _commandRuntime.CommandBuffers.SnapshotDirtyGeneration();

    private bool HaveCommandBuffersDirtiedSince(long generation)
        => _commandRuntime.CommandBuffers.HaveDirtiedSince(generation);

    internal void MarkCommandBuffersDirty([CallerMemberName] string? reason = null)
        => _commandRuntime.CommandBuffers.MarkDirty(reason);

    internal void MarkCommandBuffersDirtyForLegacyMeshState([CallerMemberName] string? reason = null)
    {
        if (VulkanPrimaryCommandBufferReuseEnabled || CommandChainsEnabledForCurrentRecording || _frameOperationQueue.CurrentThread.Capture is not null)
            return;

        MarkCommandBuffersDirty(reason);
    }

    internal override void NotifyRenderResourcesChanged()
        => InvalidateCommandChainScheduleForResourceChange(nameof(NotifyRenderResourcesChanged));

    internal override void NotifyRenderResourcesChanged(string? reason)
        => InvalidateCommandChainScheduleForResourceChange(
            RenderResourceChangeKind.BindingIdentity,
            string.IsNullOrWhiteSpace(reason)
                ? nameof(NotifyRenderResourcesChanged)
                : reason);

    internal override void NotifyRenderResourcesChanged(RenderResourceChangeKind kind, string? reason)
        => InvalidateCommandChainScheduleForResourceChange(
            kind,
            string.IsNullOrWhiteSpace(reason)
                ? nameof(NotifyRenderResourcesChanged)
                : reason);

}
