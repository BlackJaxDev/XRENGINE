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
    private long _commandBufferDirtyGeneration;
    private long _lastCommandBufferDirtyTimestamp;

    private long SnapshotCommandBufferDirtyGeneration()
        => Volatile.Read(ref _commandBufferDirtyGeneration);

    private bool HaveCommandBuffersDirtiedSince(long generation)
        => Volatile.Read(ref _commandBufferDirtyGeneration) != generation;

    private void MarkCommandBuffersDirty([CallerMemberName] string? reason = null)
    {
        Volatile.Write(ref _lastCommandBufferDirtyTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _commandBufferDirtyGeneration);

        if (_commandBufferDirtyFlags is null)
            return;

        for (int i = 0; i < _commandBufferDirtyFlags.Length; i++)
            _commandBufferDirtyFlags[i] = true;
        MarkCommandBufferVariantsDirty(reason);

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBuffersDirty(reason);
        TrackCommandBufferDirtyReason(reason, _commandBufferDirtyFlags.Length);
    }

    private void TrackCommandBufferDirtyReason(string? reason, int swapchainImageCount)
    {
        string key = string.IsNullOrWhiteSpace(reason) ? "<unknown>" : reason;
        string? summary = null;
        lock (_commandBufferDirtyReasonLock)
        {
            _commandBufferDirtyReasons.TryGetValue(key, out int count);
            _commandBufferDirtyReasons[key] = count + 1;

            long now = Stopwatch.GetTimestamp();
            if (_lastCommandBufferDirtyReasonLogTimestamp == 0)
            {
                _lastCommandBufferDirtyReasonLogTimestamp = now;
                return;
            }

            if (Stopwatch.GetElapsedTime(_lastCommandBufferDirtyReasonLogTimestamp, now) < TimeSpan.FromSeconds(1))
                return;

            StringBuilder builder = new();
            foreach (KeyValuePair<string, int> pair in _commandBufferDirtyReasons.OrderByDescending(static p => p.Value))
            {
                if (builder.Length > 0)
                    builder.Append(", ");

                builder.Append(pair.Key).Append('=').Append(pair.Value);
            }

            summary = builder.ToString();
            _commandBufferDirtyReasons.Clear();
            _lastCommandBufferDirtyReasonLogTimestamp = now;
        }

        Debug.Vulkan(
            "[Vulkan] Command buffers marked dirty over the last second. SwapchainImages={0} Reasons={1}",
            swapchainImageCount,
            summary);
    }

    internal void MarkCommandBuffersDirtyForLegacyMeshState([CallerMemberName] string? reason = null)
    {
        if (VulkanPrimaryCommandBufferReuseEnabled || CommandChainsEnabledForCurrentRecording || t_frameOpCapture is not null)
            return;

        MarkCommandBuffersDirty(reason);
    }

    internal override void NotifyRenderResourcesChanged()
        => InvalidateCommandChainScheduleForResourceChange(nameof(NotifyRenderResourcesChanged));

    internal override void NotifyRenderResourcesChanged(string? reason)
        => InvalidateCommandChainScheduleForResourceChange(
            string.IsNullOrWhiteSpace(reason)
                ? nameof(NotifyRenderResourcesChanged)
                : reason);

}
