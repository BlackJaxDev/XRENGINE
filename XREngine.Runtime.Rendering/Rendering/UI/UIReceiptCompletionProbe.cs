namespace XREngine.Rendering.UI;

/// <summary>
/// A shared, monotonic retirement proof for one required UI producer receipt.
/// The backend may consume its receipt on the first successful query, so GPU
/// cohorts and an offscreen producer must use this same probe.
/// </summary>
internal sealed class UIReceiptCompletionProbe
{
    private readonly object _gate = new();
    private UIReceiptRetirementLatch _retirement;
    private bool _sealed;
    private bool _authored;
    private bool _gpuSignaled;
    private bool? _terminalCompletion;
    private XRGpuFence? _fence;
    private long _fenceRentalId;
    private readonly Func<bool> _retirementQuery;

    internal AbstractRenderer Renderer { get; }
    internal long BackendGeneration { get; }
    internal ulong ReceiptId { get; }

    internal UIReceiptCompletionProbe(AbstractRenderer renderer, ulong receiptId)
    {
        Renderer = renderer;
        BackendGeneration = renderer.BackendGeneration;
        ReceiptId = receiptId;
        _retirementQuery = () => renderer.IsUiPublicationReceiptRetired(receiptId);
    }

    // Allows the receipt/fence state machine to be exercised without a GPU backend.
    internal UIReceiptCompletionProbe(Func<bool> retirementQuery)
    {
        Renderer = null!;
        _retirementQuery = retirementQuery;
    }

    internal void Seal(bool authored, XRGpuFence? fence)
    {
        lock (_gate)
        {
            _authored = authored;
            _fence = fence;
            _fenceRentalId = fence?.DiagnosticRentalId ?? 0;
            _sealed = true;
        }
    }

    internal bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                ObserveRetirement();
                return _terminalCompletion == true;
            }
        }
    }

    internal bool IsRetired
    {
        get
        {
            lock (_gate)
            {
                ObserveRetirement();
                return _retirement.IsRetired;
            }
        }
    }

    private void ObserveFence()
    {
        if (_gpuSignaled || _fence is not { IsDisposed: false } fence ||
            (_fenceRentalId != 0 && fence.DiagnosticRentalId != _fenceRentalId) ||
            fence.SubmissionStatus != EGpuFenceSubmissionStatus.Submitted)
            return;
        _gpuSignaled = fence.Poll() == EGpuFenceStatus.Signaled;
    }

    private void ObserveRetirement()
    {
        if (_retirement.IsRetired)
            return;
        ObserveFence();
        if (_retirement.ShouldQuery)
            _retirement.Observe(_retirementQuery());
        if (!_retirement.IsRetired)
            return;
        ObserveFence();
        // The collector owns and may pool the fence immediately after this
        // retirement proof. Freeze the outcome before that wrapper is reused.
        _terminalCompletion = _sealed && _authored && _gpuSignaled;
        _fence = null;
    }
}
