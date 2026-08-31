using System;

namespace XREngine.Rendering;

/// <summary>
/// Cold opt-in receipts for the shadow-pipeline guards that can suppress atlas
/// command authoring before a backend records a native writer.
/// </summary>
public static class DirectionalShadowPipelineDiagnostics
{
    private const int ReceiptCapacity = 16;
    private static readonly object Sync = new();
    private static readonly DirectionalShadowPipelineReceipt[] Receipts = new DirectionalShadowPipelineReceipt[ReceiptCapacity];
    private static int _receiptCount;

    private static bool IsEnabled => XREnvironment.IsEnabled(XREngineEnvironmentVariables.DirectionalShadowAudit);

    internal static void Record(
        EDirectionalShadowPipelineReceiptStage stage,
        XRRenderPipelineInstance pipelineInstance,
        RenderPipeline? pipeline,
        XRFrameBuffer? target,
        string? failure = null)
    {
        if (!IsEnabled ||
            pipeline is not XREngine.Components.Lights.ShadowRenderPipeline ||
            target?.Name is not { } targetName ||
            !targetName.Contains("ShadowAtlas_Directional", StringComparison.Ordinal))
        {
            return;
        }

        lock (Sync)
        {
            if (_receiptCount >= ReceiptCapacity)
                return;

            Receipts[_receiptCount++] = new DirectionalShadowPipelineReceipt(
                stage,
                pipelineInstance.GetHashCode(),
                target.GetHashCode(),
                targetName,
                failure);
        }
    }

    /// <summary>Returns a point-in-time copy of the fixed-capacity receipts.</summary>
    public static DirectionalShadowPipelineDiagnosticsSnapshot GetSnapshot()
    {
        lock (Sync)
        {
            var receipts = new DirectionalShadowPipelineReceipt[_receiptCount];
            Array.Copy(Receipts, receipts, _receiptCount);
            return new DirectionalShadowPipelineDiagnosticsSnapshot(IsEnabled, receipts);
        }
    }
}
