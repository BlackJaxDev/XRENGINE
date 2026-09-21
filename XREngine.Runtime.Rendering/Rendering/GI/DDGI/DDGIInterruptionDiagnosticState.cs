namespace XREngine.Rendering.GI.DDGI;

#if !XRE_PUBLISHED
/// <summary>Correlates one requested Visibility interruption with its GPU receipt.</summary>
internal sealed class DDGIInterruptionDiagnosticState
{
    private readonly XRRenderPipelineInstance _pipeline;
    private readonly ulong _requestToken;
    private readonly int _pipelineInstanceId;
    private readonly int _resourceGeneration;
    private int _remainingSkips;
    private ulong _lastConsumedRenderFrame = ulong.MaxValue;
    private bool _interruptedCycleAwaitingAbort;
    private bool _diagnosticAbortReceiptPending;
    private ulong? _firstSkippedRenderFrame;
    private ulong? _lastSkippedRenderFrame;
    private uint? _firstSkippedStateFrameIndex;
    private uint? _lastSkippedStateFrameIndex;
    private int _matchedAbortCount;
    private int _acceptedNonPublishingReceiptCount;
    private int _unexpectedCompleteCount;
    private int _unexpectedPublicationCount;
    private ulong? _firstAcceptedRecoveryRenderFrame;
    private uint? _firstAcceptedRecoveryStateFrameIndex;
    private bool _failedReceiptLatched;

    public DDGIInterruptionDiagnosticState(ulong requestToken, XRRenderPipelineInstance pipeline, int pipelineInstanceId, int resourceGeneration, int remainingSkips)
    {
        _requestToken = requestToken;
        _pipeline = pipeline;
        _pipelineInstanceId = pipelineInstanceId;
        _resourceGeneration = resourceGeneration;
        _remainingSkips = remainingSkips;
    }

    public bool Matches(XRRenderPipelineInstance pipeline)
        => ReferenceEquals(_pipeline, pipeline) && _pipeline.ResourceGeneration == _resourceGeneration;

    public bool ShouldSkip(ulong renderFrame, uint stateFrameIndex)
    {
        if (_interruptedCycleAwaitingAbort)
            return true;
        if (_remainingSkips <= 0)
            return false;
        if (_lastConsumedRenderFrame == renderFrame)
            return true;

        _lastConsumedRenderFrame = renderFrame;
        _remainingSkips--;
        _firstSkippedRenderFrame ??= renderFrame;
        _lastSkippedRenderFrame = renderFrame;
        _firstSkippedStateFrameIndex ??= stateFrameIndex;
        _lastSkippedStateFrameIndex = stateFrameIndex;
        _interruptedCycleAwaitingAbort = true;
        return true;
    }

    public void RecordAbort()
    {
        if (!_interruptedCycleAwaitingAbort)
            return;
        _interruptedCycleAwaitingAbort = false;
        _diagnosticAbortReceiptPending = true;
        _matchedAbortCount++;
    }

    public void RecordAcceptedReceipt()
    {
        if (!_diagnosticAbortReceiptPending)
            return;
        _diagnosticAbortReceiptPending = false;
        _acceptedNonPublishingReceiptCount++;
    }

    public void RecordReceiptFailure()
    {
        if (!_diagnosticAbortReceiptPending)
            return;
        _diagnosticAbortReceiptPending = false;
        _failedReceiptLatched = true;
    }

    public void RecordComplete()
    {
        if (_interruptedCycleAwaitingAbort)
            _unexpectedCompleteCount++;
    }

    public void RecordPublication(ulong renderFrame, uint stateFrameIndex)
    {
        if (_interruptedCycleAwaitingAbort)
        {
            _unexpectedPublicationCount++;
            return;
        }
        if (_remainingSkips > 0 || _diagnosticAbortReceiptPending || _acceptedNonPublishingReceiptCount == 0)
            return;
        _firstAcceptedRecoveryRenderFrame ??= renderFrame;
        _firstAcceptedRecoveryStateFrameIndex ??= stateFrameIndex;
    }

    public DDGIInterruptionDiagnosticSnapshot Capture()
        => new(
            _requestToken,
            _pipelineInstanceId,
            _resourceGeneration,
            _remainingSkips,
            _firstSkippedRenderFrame,
            _lastSkippedRenderFrame,
            _firstSkippedStateFrameIndex,
            _lastSkippedStateFrameIndex,
            _matchedAbortCount,
            _acceptedNonPublishingReceiptCount,
            _unexpectedCompleteCount,
            _unexpectedPublicationCount,
            _firstAcceptedRecoveryRenderFrame,
            _firstAcceptedRecoveryStateFrameIndex,
            _failedReceiptLatched);
}
#endif
