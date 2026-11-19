using System;
using System.Text;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    private static string? s_firstMissingTerminalWrite;
    private static int s_capturingMissingTerminalWrite;

    /// <summary>Reads the first rejected desktop terminal operation summary.</summary>
    public static string? GetFirstMissingTerminalWrite()
        => Volatile.Read(ref s_firstMissingTerminalWrite);

    private static void CaptureMissingTerminalWrite(
        scoped ref PrimaryCommandBufferRecordingState state)
    {
        if (Interlocked.CompareExchange(ref s_capturingMissingTerminalWrite, 1, 0) != 0)
            return;

        // Allocate only for the first failure. Successful and repeated failed frames
        // do not format diagnostics or retain the frame's resource owners.
        StringBuilder text = new(1024);
        int swapchainDraws = 0;
        for (int i = 0; i < state.Ops.Length; i++)
            if (state.Ops.GetTarget(i) is null &&
                state.Ops.GetHeader(i).OpCode == EVulkanPrimaryPlanNodeKind.MeshDraw)
                swapchainDraws++;

        text.Append("image=").Append(state.ImageIndex)
            .Append(" slot=").Append(state.FrameDataSlotIndex)
            .Append(" ops=").Append(state.Ops.Length)
            .Append(" swapchainDraws=").Append(swapchainDraws)
            .Append(" deferredOps=").Append(state.PipelineDeferredOperationIndices.Count)
            .Append(" draws=").Append(state.Metrics.DrawCount)
            .Append(" blits=").Append(state.Metrics.BlitCount)
            .Append(" dropped=").Append(state.Metrics.DroppedFrameOps)
            .Append(" firstFailure=").Append(state.Metrics.FirstFailure?.Message);

        for (int i = Math.Max(0, state.Ops.Length - 8); i < state.Ops.Length; i++)
        {
            ref readonly var header = ref state.Ops.GetHeader(i);
            ref readonly var context = ref state.Ops.GetContext(i);
            text.AppendLine().Append(i).Append(':').Append(header.OpCode)
                .Append(" pass=").Append(header.PassIndex)
                .Append(" target=").Append(state.Ops.GetTarget(i)?.Name ?? "<swapchain>")
                .Append(" pipeline=").Append(context.PipelineInstance?.DebugName)
                .Append(" output=").Append(context.OutputTargetName)
                .Append(" deferred=").Append(state.PipelineDeferredOperationIndices.Contains(i));
            if (header.OpCode == EVulkanPrimaryPlanNodeKind.MeshDraw)
            {
                PendingMeshDraw draw = state.Ops.GetMeshDraw(i).Draw;
                text.Append(" material=").Append((draw.MaterialOverride ?? draw.Renderer.MeshRenderer.Material)?.Name);
            }
        }
        Volatile.Write(ref s_firstMissingTerminalWrite, text.ToString());
    }
}
