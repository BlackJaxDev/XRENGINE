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
    private void MarkCommandBuffersDirty([CallerMemberName] string? reason = null)
    {
        if (_commandBufferDirtyFlags is null)
            return;

        for (int i = 0; i < _commandBufferDirtyFlags.Length; i++)
            _commandBufferDirtyFlags[i] = true;
        MarkCommandBufferVariantsDirty();

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
        if (CommandChainsEnabled)
            return;

        MarkCommandBuffersDirty(reason);
    }

    internal override void NotifyRenderResourcesChanged()
        => MarkCommandBuffersDirty();

}
