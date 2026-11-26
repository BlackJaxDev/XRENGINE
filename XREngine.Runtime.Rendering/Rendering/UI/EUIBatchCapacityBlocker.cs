namespace XREngine.Rendering.UI;

/// <summary>
/// Why <see cref="UIBatchCollector.CanRenderPublishedBatches"/> last refused to draw a canvas. The canvas reports
/// the reason in its <c>UI.Canvas.BatchCapacityPending</c> warning, so a stall shows which resource it waits for.
/// </summary>
public enum EUIBatchCapacityBlocker
{
    /// <summary>The last check passed.</summary>
    None,
    /// <summary>Too many draw receipts wait for a frame owner to claim them.</summary>
    ReceiptLeaseLimit,
    /// <summary>Every GPU cohort of one run still holds a draw receipt that its frame has not retired.</summary>
    ReceiptNotRetired,
    /// <summary>Every GPU cohort of one run still waits for its submission fence.</summary>
    FenceNotComplete,
    /// <summary>Every GPU cohort of one run still has a buffer that the backend does not allow to be rewritten.</summary>
    BufferNotReusable,
    /// <summary>The canvas would need more GPU cohorts than its budget allows.</summary>
    CohortBudget,
}
