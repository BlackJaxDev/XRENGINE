namespace XREngine.Rendering.WebGPU;

/// <summary>A nonblocking receipt backed by the owning WebGPU queue's submitted-work promise.</summary>
internal sealed class WebGpuFence(bool requiresFrameSubmission) : XRGpuFence
{
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _completion;
    private bool _failed;

    public override EGpuFenceSubmissionStatus SubmissionStatus => _failed
        ? EGpuFenceSubmissionStatus.Failed
        : _completion is null ? EGpuFenceSubmissionStatus.AwaitingSubmission : EGpuFenceSubmissionStatus.Submitted;

    internal void Arm(WebGpuRendererHost renderer, bool submittedFrame)
    {
        if (IsDisposed || _completion is not null || _failed) return;
        if (renderer.State != BrowserRendererState.Ready || requiresFrameSubmission && !submittedFrame)
        {
            _failed = true;
            return;
        }
        _completion = ObserveCompletionAsync(renderer, _cancellation.Token);
    }

    private async Task ObserveCompletionAsync(WebGpuRendererHost renderer, CancellationToken cancellationToken)
    {
        try { await renderer.CompleteSubmittedWorkAsync(cancellationToken); }
        catch { _failed = true; }
    }

    protected override EGpuFenceStatus PollCore()
        => _failed ? EGpuFenceStatus.Failed
            : _completion?.IsCompletedSuccessfully == true ? EGpuFenceStatus.Signaled : EGpuFenceStatus.Pending;

    protected override void DisposeCore()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
