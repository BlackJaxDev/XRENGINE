using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Diagnostics;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    private const string AdvancedVisibilityCounterReceiptPrefix = "AdvancedVisibilityCounters";

    [XRMcp(Name = "set_advanced_visibility_counter_sampling", Permission = McpPermissionLevel.Mutate, PermissionReason = "Starts or stops delayed GPU counter copies in the running renderer.")]
    [Description("Set how often the Advanced pipeline copies its per-view visibility counters under submission strategies that are not instrumented. The copy is delayed until the frame completes, so it adds no GPU or CPU wait. 0 turns sampling off and 1 samples every frame. Read the values with get_advanced_visibility_counters.")]
    public static Task<McpToolResponse> SetAdvancedVisibilityCounterSamplingAsync(
        [McpName("interval_frames"), Description("Render frames between two samples. 0 turns sampling off. 1 samples every frame.")] int intervalFrames)
    {
        if (intervalFrames < 0)
            return Task.FromResult(new McpToolResponse("interval_frames must be 0 or greater.", isError: true));

        AdvancedVisibilityCounterSampling.IntervalFrames = intervalFrames;
        return GetAdvancedVisibilityCountersAsync();
    }

    [XRMcp(Name = "get_advanced_visibility_counters", Permission = McpPermissionLevel.ReadOnly)]
    [Description("Return the newest completed Advanced visibility counter values for each output and view, including payloadOverflow and decodeOutOfBounds, plus the sampling state. Instrumented strategies copy the counters every frame. Other strategies copy them only while set_advanced_visibility_counter_sampling is on.")]
    public static Task<McpToolResponse> GetAdvancedVisibilityCountersAsync()
    {
        AdvancedGpuCounterReadback[] receipts = AdvancedGpuCounterReadbacks.CaptureSnapshot();
        EMeshSubmissionStrategy strategy = RuntimeEngine.Rendering.LastResolvedMeshSubmissionStrategy;
        bool instrumented = GpuDiagnosticReadbackPlan.IsInstrumented(strategy);

        // The snapshot is newest-first, so the first receipt for an output and view is its newest.
        var newestByView = new List<AdvancedGpuCounterReadback>();
        var seen = new HashSet<(ulong OutputId, uint ViewId)>();
        foreach (AdvancedGpuCounterReadback receipt in receipts)
        {
            if (!receipt.Source.StartsWith(AdvancedVisibilityCounterReceiptPrefix, StringComparison.Ordinal))
                continue;
            if (seen.Add((receipt.OutputId, receipt.ViewId)))
                newestByView.Add(receipt);
        }

        ulong currentFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
        var views = newestByView
            .OrderBy(static receipt => receipt.OutputId)
            .ThenBy(static receipt => receipt.ViewId)
            .Select(receipt => new
            {
                receipt.OutputId,
                receipt.ViewId,
                receipt.FrameId,
                framesOld = currentFrameId >= receipt.FrameId ? currentFrameId - receipt.FrameId : 0UL,
                receipt.ResourceGeneration,
                earlyDraws = Word(receipt, 0),
                deferredCandidates = Word(receipt, 1),
                lateDraws = Word(receipt, 2),
                recoveredCandidates = Word(receipt, 3),
                validPixels = Word(receipt, 4),
                invalidPixels = Word(receipt, 5),
                receipt.PayloadOverflow,
                maskedCoveragePixels = Word(receipt, 7),
                receipt.DecodeOutOfBounds,
                depthPyramidBuilds = Word(receipt, 9),
                receipt.UnsupportedDisplacement,
                receipt.PhysicsChainBoundsRouteRejected,
                receipt.PhysicsChainBoundsNumericRejected,
                receipt.MeshletEarlyVisible,
                receipt.MeshletEarlyFrustumCulled,
                receipt.MeshletEarlyConeCulled,
                receipt.MeshletEarlyHistoryCulled,
                receipt.MeshletEarlyTriangles,
            })
            .ToArray();

        bool anyErrors = newestByView.Any(static receipt => receipt.PayloadOverflow != 0u || receipt.DecodeOutOfBounds != 0u);
        string message = views.Length == 0
            ? instrumented || AdvancedVisibilityCounterSampling.IsEnabled
                ? "No completed Advanced visibility counter receipts yet. Wait a few frames and read again."
                : "No Advanced visibility counter receipts. Turn on sampling with set_advanced_visibility_counter_sampling."
            : anyErrors
                ? "Advanced visibility counters report payload overflow or out-of-bounds decodes."
                : "Advanced visibility counters report no payload overflow and no out-of-bounds decodes.";

        return Task.FromResult(new McpToolResponse(message, new
        {
            sampling = new
            {
                intervalFrames = AdvancedVisibilityCounterSampling.IntervalFrames,
                enabled = AdvancedVisibilityCounterSampling.IsEnabled,
                effectiveStrategy = strategy.ToString(),
                instrumentedStrategyCopiesEveryFrame = instrumented,
                environmentVariable = XREngineEnvironmentVariables.AdvancedVisibilityCounterSampling,
            },
            currentFrameId,
            anyPayloadOverflowOrDecodeOutOfBounds = anyErrors,
            views,
            attribution = "Each row is the newest completed receipt for one output and view. Values count events of that one source frame; they are not totals over time.",
        }));

        static uint Word(AdvancedGpuCounterReadback receipt, int index)
            => receipt.Values.Length > index ? receipt.Values[index] : 0u;
    }
}
