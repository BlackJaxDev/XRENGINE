using ImGuiNET;
using System;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanImGuiDrawDataCache
{
    private readonly object _gate = new();
    private VulkanImGuiFrameSnapshot? _snapshot;
    private VulkanImGuiFrameSnapshot? _recycledSnapshot;

    public void Store(ImDrawDataPtr drawData)
    {
        lock (_gate)
        {
            VulkanImGuiFrameSnapshot snapshot =
                _snapshot ??
                _recycledSnapshot ??
                new VulkanImGuiFrameSnapshot();
            if (ReferenceEquals(snapshot, _recycledSnapshot))
                _recycledSnapshot = null;

            snapshot.Capture(drawData);
            _snapshot = snapshot;
        }
    }

    public bool TryConsume(out VulkanImGuiFrameSnapshot? snapshot)
    {
        lock (_gate)
        {
            snapshot = _snapshot;
            _snapshot = null;
            return snapshot is not null;
        }
    }

    public void Recycle(VulkanImGuiFrameSnapshot? snapshot)
    {
        if (snapshot is null)
            return;

        lock (_gate)
        {
            if (ReferenceEquals(snapshot, _snapshot) ||
                ReferenceEquals(snapshot, _recycledSnapshot))
            {
                return;
            }

            _recycledSnapshot = snapshot;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _snapshot = null;
            _recycledSnapshot = null;
        }
    }
}
