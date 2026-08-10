using ImGuiNET;
using System;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanImGuiDrawDataCache
{
    private readonly object _gate = new();
    private VulkanImGuiFrameSnapshot? _pendingSnapshot;
    private VulkanImGuiFrameSnapshot? _retainedSnapshot;
    private VulkanImGuiFrameSnapshot? _recycledSnapshot;

    public void Store(ImDrawDataPtr drawData)
    {
        // A render command can be evaluated more than once while Vulkan plans
        // and records a frame. Do not let a later empty ImGui frame overwrite a
        // renderable editor snapshot that the frame loop has not consumed yet.
        if (drawData.CmdListsCount <= 0 ||
            drawData.TotalVtxCount <= 0 ||
            drawData.TotalIdxCount <= 0 ||
            drawData.DisplaySize.X <= 0f ||
            drawData.DisplaySize.Y <= 0f)
        {
            return;
        }

        lock (_gate)
        {
            VulkanImGuiFrameSnapshot snapshot =
                _pendingSnapshot ??
                _recycledSnapshot ??
                new VulkanImGuiFrameSnapshot();
            if (ReferenceEquals(snapshot, _recycledSnapshot))
                _recycledSnapshot = null;

            snapshot.Capture(drawData);
            _pendingSnapshot = snapshot;
        }
    }

    public bool TryConsume(out VulkanImGuiFrameSnapshot? snapshot)
    {
        lock (_gate)
        {
            snapshot = _pendingSnapshot ?? _retainedSnapshot;
            if (ReferenceEquals(snapshot, _pendingSnapshot))
                _pendingSnapshot = null;
            return snapshot is not null;
        }
    }

    public void Recycle(VulkanImGuiFrameSnapshot? snapshot)
    {
        if (snapshot is null)
            return;

        lock (_gate)
        {
            if (ReferenceEquals(snapshot, _pendingSnapshot) ||
                ReferenceEquals(snapshot, _retainedSnapshot))
                return;

            VulkanImGuiFrameSnapshot? previouslyRetained = _retainedSnapshot;
            _retainedSnapshot = snapshot;
            if (previouslyRetained is not null &&
                !ReferenceEquals(previouslyRetained, _pendingSnapshot))
            {
                _recycledSnapshot = previouslyRetained;
            }
        }
    }

    public void Discard(VulkanImGuiFrameSnapshot? snapshot)
    {
        if (snapshot is null)
            return;

        lock (_gate)
        {
            if (ReferenceEquals(snapshot, _pendingSnapshot))
                _pendingSnapshot = null;
            if (ReferenceEquals(snapshot, _retainedSnapshot))
                _retainedSnapshot = null;

            _recycledSnapshot = snapshot;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pendingSnapshot = null;
            _retainedSnapshot = null;
            _recycledSnapshot = null;
        }
    }
}
