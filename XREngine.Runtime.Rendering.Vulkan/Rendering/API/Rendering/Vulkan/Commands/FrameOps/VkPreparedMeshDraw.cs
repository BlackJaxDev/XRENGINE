using XREngine.Rendering.Vulkan.RenderGraph;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable mesh-draw input copied out of the mutable frame-operation graph
/// before command-chain workers are released.
/// </summary>
internal readonly record struct VkPreparedMeshDraw(
    int SourceOpIndex,
    Viewport Viewport,
    Rect2D Scissor,
    Viewport[]? IndexedViewports,
    Rect2D[]? IndexedScissors,
    uint ViewportScissorCount,
    FrameOpContext Context,
    int UniformSlot,
    string DiagnosticMeshName = "<unnamed mesh>")
{
    internal VulkanPreparedMeshDrawState RecordingState { get; init; }
    internal XRFrameBuffer? Target { get; init; }
    internal bool OwnsIndexedViewportArrays { get; init; }
    internal VkMeshRenderer OwnerIdentity => RecordingState.OwnerIdentity;

    internal static bool TryCreateOwned(
        int sourceOpIndex,
        in PendingMeshDraw source,
        in FrameOpContext context,
        XRFrameBuffer? target,
        int uniformSlot,
        in VulkanPreparedMeshDrawState recordingState,
        out VkPreparedMeshDraw prepared,
        out string reason)
    {
        prepared = default;
        reason = "Ready";
        uint viewportScissorCount = Math.Max(source.ViewportScissorCount, 1u);
        Viewport[]? indexedViewports = null;
        Rect2D[]? indexedScissors = null;
        bool ownsIndexedArrays = false;

        if (viewportScissorCount > 1)
        {
            if (viewportScissorCount > int.MaxValue)
            {
                reason = $"viewport/scissor count {viewportScissorCount} exceeds the supported managed range";
                return false;
            }

            int count = (int)viewportScissorCount;
            if (source.IndexedViewports is not { } sourceViewports ||
                source.IndexedScissors is not { } sourceScissors ||
                sourceViewports.Length < count ||
                sourceScissors.Length < count)
            {
                reason = $"viewport/scissor count {viewportScissorCount} has no complete indexed snapshot";
                return false;
            }

            indexedViewports = recordingState.OwnerIdentity.RentPreparedViewportArray(count);
            try
            {
                indexedScissors = recordingState.OwnerIdentity.RentPreparedScissorArray(count);
            }
            catch
            {
                recordingState.OwnerIdentity.ReturnPreparedViewportArray(indexedViewports);
                throw;
            }
            sourceViewports.AsSpan(0, count).CopyTo(indexedViewports);
            sourceScissors.AsSpan(0, count).CopyTo(indexedScissors);
            ownsIndexedArrays = true;
        }

        prepared = new VkPreparedMeshDraw(
            sourceOpIndex,
            source.Viewport,
            source.Scissor,
            indexedViewports,
            indexedScissors,
            viewportScissorCount,
            context,
            uniformSlot,
            source.Renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>")
        {
            RecordingState = recordingState,
            Target = target,
            OwnsIndexedViewportArrays = ownsIndexedArrays,
        };
        return true;
    }

    internal void Release()
    {
        VkMeshRenderer.ReturnPreparedMeshDrawStateBuffers(RecordingState);
        if (!OwnsIndexedViewportArrays)
            return;

        if (IndexedViewports is not null)
            OwnerIdentity.ReturnPreparedViewportArray(IndexedViewports);
        if (IndexedScissors is not null)
            OwnerIdentity.ReturnPreparedScissorArray(IndexedScissors);
    }
}
