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

internal sealed partial class VulkanCommandRuntime
{
    private ref long _commandBufferDirtyGeneration => ref CommandBuffers.DirtyGeneration;
    private ref long _lastCommandBufferDirtyTimestamp => ref CommandBuffers.LastDirtyTimestamp;

    private long SnapshotCommandBufferDirtyGeneration()
        => CommandBuffers.SnapshotDirtyGeneration();

    private bool HaveCommandBuffersDirtiedSince(long generation)
        => CommandBuffers.HaveDirtiedSince(generation);

    internal void MarkCommandBuffersDirty([CallerMemberName] string? reason = null)
        => CommandBuffers.MarkDirty(reason);

    internal void MarkCommandBuffersDirtyForLegacyMeshState([CallerMemberName] string? reason = null)
    {
        if (VulkanPrimaryCommandBufferReuseEnabled || CommandChainsEnabledForCurrentRecording)
            return;

        MarkCommandBuffersDirty(reason);
    }

    internal void NotifyRenderResourcesChanged()
        => InvalidateCommandChainScheduleForResourceChange(nameof(NotifyRenderResourcesChanged));

    internal void NotifyRenderResourcesChanged(string? reason)
        => InvalidateCommandChainScheduleForResourceChange(
            RenderResourceChangeKind.BindingIdentity,
            string.IsNullOrWhiteSpace(reason)
                ? nameof(NotifyRenderResourcesChanged)
                : reason);

    internal void NotifyRenderResourcesChanged(RenderResourceChangeKind kind, string? reason)
        => InvalidateCommandChainScheduleForResourceChange(
            kind,
            string.IsNullOrWhiteSpace(reason)
                ? nameof(NotifyRenderResourcesChanged)
                : reason);

}
