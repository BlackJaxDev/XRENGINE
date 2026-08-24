using XREngine.Rendering.Vulkan.RenderGraph;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Compact hot draw header. Reference-heavy audit data lives in the frame recording sidecar.</summary>
internal readonly record struct VkPreparedMeshDraw(
    int SourceOpIndex,
    Viewport Viewport,
    Rect2D Scissor,
    VulkanPreparedStreamRange IndexedViewports,
    VulkanPreparedStreamRange IndexedScissors,
    uint ViewportScissorCount,
    int UniformSlot,
    VulkanPreparedMeshDrawState RecordingState)
{
    internal static bool TryCreate(
        VulkanPreparedFrameRecording recording,
        int sourceOpIndex,
        in PendingMeshDraw source,
        int uniformSlot,
        in VulkanPreparedMeshDrawState recordingState,
        out VkPreparedMeshDraw prepared,
        out string reason)
    {
        prepared = default;
        reason = "Ready";
        uint count = Math.Max(source.ViewportScissorCount, 1u);
        VulkanPreparedStreamRange viewports = default;
        VulkanPreparedStreamRange scissors = default;
        if (count > 1)
        {
            if (count > int.MaxValue || source.IndexedViewports is not { } sourceViewports || source.IndexedScissors is not { } sourceScissors || sourceViewports.Length < (int)count || sourceScissors.Length < (int)count)
            {
                reason = $"viewport/scissor count {count} has no complete indexed snapshot";
                return false;
            }
            viewports = recording.AppendViewports(sourceViewports.AsSpan(0, (int)count));
            scissors = recording.AppendScissors(sourceScissors.AsSpan(0, (int)count));
        }

        prepared = new(sourceOpIndex, source.Viewport, source.Scissor, viewports, scissors, count, uniformSlot, recordingState);
        return true;
    }
}
