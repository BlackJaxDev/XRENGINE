using System.Text;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct ResourcePlannerSignatureBreakdown(
    EVulkanFrameOpContextKind ContextKind,
    ulong ContextId,
    ulong CompatibilityFingerprint,
    int Registry,
    int OutputFrameBuffer,
    int OutputTarget,
    uint DisplayWidth,
    uint DisplayHeight,
    uint InternalWidth,
    uint InternalHeight,
    int PassMetadata,
    int GraphBatches,
    int GraphEdges,
    ulong ResourceGeneration,
    ulong DescriptorGeneration,
    uint SubmissionQueueFamily,
    uint GraphicsQueueFamily,
    uint ComputeQueueFamily,
    uint TransferQueueFamily)
{
    public override string ToString()
        => $"kind={ContextKind} contextId={ContextId} plan=0x{CompatibilityFingerprint:X16} registry=0x{Registry:X8} outputFbo=0x{OutputFrameBuffer:X8} outputTarget=0x{OutputTarget:X8} dims={DisplayWidth}x{DisplayHeight}/{InternalWidth}x{InternalHeight} " +
           $"passes=0x{PassMetadata:X8} batches=0x{GraphBatches:X8} edges=0x{GraphEdges:X8} resourceGen={ResourceGeneration} descriptorGen={DescriptorGeneration} submitQ={SubmissionQueueFamily} " +
           $"queues=g{GraphicsQueueFamily}/c{ComputeQueueFamily}/t{TransferQueueFamily}";

    public string DescribeDelta(in ResourcePlannerSignatureBreakdown previous)
    {
        StringBuilder builder = new();
        AppendDelta(builder, "context-kind", (int)previous.ContextKind, (int)ContextKind);
        AppendDelta(builder, "plan-fingerprint", previous.CompatibilityFingerprint, CompatibilityFingerprint, hexadecimal: true);
        AppendDelta(builder, "resource-registry", previous.Registry, Registry, hexadecimal: true);
        AppendDelta(builder, "output-fbo", previous.OutputFrameBuffer, OutputFrameBuffer, hexadecimal: true);
        AppendDelta(builder, "output-target", previous.OutputTarget, OutputTarget, hexadecimal: true);
        AppendDelta(builder, "display-width", previous.DisplayWidth, DisplayWidth);
        AppendDelta(builder, "display-height", previous.DisplayHeight, DisplayHeight);
        AppendDelta(builder, "internal-width", previous.InternalWidth, InternalWidth);
        AppendDelta(builder, "internal-height", previous.InternalHeight, InternalHeight);
        AppendDelta(builder, "pass-metadata", previous.PassMetadata, PassMetadata, hexadecimal: true);
        AppendDelta(builder, "graph-batches", previous.GraphBatches, GraphBatches, hexadecimal: true);
        AppendDelta(builder, "graph-edges", previous.GraphEdges, GraphEdges, hexadecimal: true);
        AppendDelta(builder, "resource-generation", previous.ResourceGeneration, ResourceGeneration);
        AppendDelta(builder, "descriptor-generation", previous.DescriptorGeneration, DescriptorGeneration);
        AppendDelta(builder, "submission-queue-family", previous.SubmissionQueueFamily, SubmissionQueueFamily);
        AppendDelta(builder, "graphics-queue-family", previous.GraphicsQueueFamily, GraphicsQueueFamily);
        AppendDelta(builder, "compute-queue-family", previous.ComputeQueueFamily, ComputeQueueFamily);
        AppendDelta(builder, "transfer-queue-family", previous.TransferQueueFamily, TransferQueueFamily);
        return builder.Length == 0 ? "none" : builder.ToString();
    }

    private static void AppendDelta(StringBuilder builder, string name, int oldValue, int newValue, bool hexadecimal = false)
    {
        if (oldValue == newValue)
            return;

        AppendDeltaPrefix(builder);
        if (hexadecimal)
            builder.Append(name).Append("=0x").Append(oldValue.ToString("X8")).Append("->0x").Append(newValue.ToString("X8"));
        else
            builder.Append(name).Append('=').Append(oldValue).Append("->").Append(newValue);
    }

    private static void AppendDelta(StringBuilder builder, string name, uint oldValue, uint newValue)
    {
        if (oldValue == newValue)
            return;

        AppendDeltaPrefix(builder);
        builder.Append(name).Append('=').Append(oldValue).Append("->").Append(newValue);
    }

    private static void AppendDelta(StringBuilder builder, string name, ulong oldValue, ulong newValue, bool hexadecimal = false)
    {
        if (oldValue == newValue)
            return;

        AppendDeltaPrefix(builder);
        if (hexadecimal)
            builder.Append(name).Append("=0x").Append(oldValue.ToString("X16")).Append("->0x").Append(newValue.ToString("X16"));
        else
            builder.Append(name).Append('=').Append(oldValue).Append("->").Append(newValue);
    }

    private static void AppendDeltaPrefix(StringBuilder builder)
    {
        if (builder.Length > 0)
            builder.Append(", ");
    }
}
