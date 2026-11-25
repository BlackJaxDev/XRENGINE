namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Normalizes swapchain-targeting frame operations onto one recording context.
/// </summary>
internal static class VulkanSwapchainContextCoalescer
{
    public static bool TargetsSwapchain(FrameOp operation) => operation switch
    {
        ClearOp clear => clear.Target is null,
        MeshDrawOp draw => draw.Target is null,
        QueryOp query => query.Target is null,
        BlitOp blit => blit.OutFbo is null,
        IndirectDrawOp indirect => indirect.Target is null,
        MeshTaskDispatchIndirectCountOp meshTask => meshTask.Target is null,
        TransformFeedbackOp transformFeedback => transformFeedback.Target is null,
        _ => false,
    };

    /// <summary>
    /// Returns the canonical recording context with the UI publication receipt of <paramref name="original"/>.
    /// Recording compatibility ignores the receipt, so the replacement must keep it: the receipt
    /// attests the canvas draws, and the scheduler orders screen-space UI draws by their receipt.
    /// </summary>
    private static FrameOpContext WithOwnership(in FrameOpContext canonical, in FrameOpContext original)
        => canonical with { UiPublicationReceiptId = original.UiPublicationReceiptId };

    public static void Coalesce(FrameOp[] operations)
        => Coalesce(operations.AsSpan(), preparedMeshIngress: null);

    internal static void Coalesce(
        Span<FrameOp> operations,
        VulkanPreparedMeshIngress? preparedMeshIngress)
    {
        FrameOpContext? canonicalContext = null;
        for (int index = 0; index < operations.Length; index++)
        {
            FrameOp operation = operations[index];
            if (!TargetsSwapchain(operation))
                continue;

            if (canonicalContext is null)
            {
                canonicalContext = operation.Context;
                continue;
            }

            if (!FrameOpContextCompatibility.AreRecordingCompatible(operation.Context, canonicalContext.Value))
                operation.Context = WithOwnership(canonicalContext.Value, operation.Context);
        }

        if (preparedMeshIngress is null)
            return;
        for (int index = 0; index < preparedMeshIngress.Count; index++)
        {
            ref readonly VulkanPreparedMeshIngressEntry entry =
                ref preparedMeshIngress.GetEntry(index);
            if (entry.Target is not null)
                continue;

            if (canonicalContext is null)
            {
                canonicalContext = entry.Context;
                continue;
            }

            if (!FrameOpContextCompatibility.AreRecordingCompatible(
                    entry.Context,
                    canonicalContext.Value))
            {
                preparedMeshIngress.SetContext(index, WithOwnership(canonicalContext.Value, entry.Context));
            }
        }
    }
}
