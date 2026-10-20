using XREngine.Data.Geometry;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private const int EngineViewHistoryCapacity = 8;
    private readonly EngineViewHistoryReceipt[] _engineViewHistory = new EngineViewHistoryReceipt[EngineViewHistoryCapacity];
    private uint _engineViewHistoryGeneration;

    /// <summary>Retains CPU history ownership until the exact canvas submission is accepted.</summary>
    private struct EngineViewHistoryReceipt
    {
        internal RenderFrameViewHistoryBackendReservation Reservation;
        internal XRRenderPipelineInstance? Pipeline;
        internal XRViewport? Viewport;
        internal RenderFrameOutputDescription FrameOutput;
        internal int PipelineResourceGeneration;
        internal long RendererGeneration;
        internal int Session;
        internal uint FrameSequence;
        internal bool Ready;
        internal bool ColorWritten;
    }

    internal override bool TryReserveFrameViewHistoryCandidate(
        in RenderFrameViewHistoryCandidateToken candidate,
        in RenderOutputRequest output,
        XRFrameBuffer? targetFrameBuffer,
        out RenderFrameViewHistoryBackendReservation reservation)
    {
        reservation = default;
        // The supported history output is the bound mono canvas. Intermediate
        // framebuffer passes remain valid but cannot complete another output's history.
        if (targetFrameBuffer is not null || !candidate.IsValid || !output.IsDefined ||
            State != BrowserRendererState.Ready || !AcceptsBackendWork || !_engineRecording ||
            _session <= 0 || !ReferenceEquals(Current, this) ||
            CurrentFrameOutput is not { IsValid: true } frameOutput ||
            frameOutput.ExecutionMode != RenderExecutionMode.BrowserCanvas ||
            frameOutput.Properties.Layers != 1 || frameOutput.ViewIndex != 0 ||
            frameOutput.Properties.ColorEncoding is null ||
            !TryDescribeFrameOutput(out RenderFrameOutputDescription currentOutput) || currentOutput != frameOutput ||
            _engineViewport is not { } viewport ||
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline is not { } pipeline ||
            !ReferenceEquals(viewport.RenderPipelineInstance, pipeline) ||
            output.ViewKind is not (EVrOutputViewKind.DesktopEditor or EVrOutputViewKind.CyclopeanDesktop) ||
            output.ExpectedWriteAspect != ERenderOutputWriteAspect.Color ||
            output.Target.ViewMask != 0 ||
            output.Target.TargetGeneration != unchecked((ulong)pipeline.ResourceGeneration) ||
            !MatchesEngineViewHistoryViewport(in candidate, in output, viewport) ||
            output.Target.DisplayWidth != frameOutput.Properties.Width ||
            output.Target.DisplayHeight != frameOutput.Properties.Height ||
            !MatchesActiveEngineViewHistory(in candidate, in output, pipeline, viewport))
            return false;

        int available = -1;
        for (int index = 0; index < _engineViewHistory.Length; index++)
        {
            ref readonly EngineViewHistoryReceipt receipt = ref _engineViewHistory[index];
            if (!receipt.Reservation.IsValid)
            {
                if (available < 0) available = index;
            }
            else if (receipt.Reservation.Candidate.MatchesIdentity(in candidate))
                return false;
        }
        if (available < 0)
            return false;

        uint generation = _engineViewHistoryGeneration == uint.MaxValue ? 1U : _engineViewHistoryGeneration + 1U;
        SetField(ref _engineViewHistoryGeneration, generation, publishNotifications: false);
        reservation = new(candidate, output, targetFrameBuffer, available, generation);
        _engineViewHistory[available] = new EngineViewHistoryReceipt
        {
            Reservation = reservation,
            Pipeline = pipeline,
            Viewport = viewport,
            // The request generation belongs to pipeline resources; the frame
            // description separately freezes the canvas surface generation.
            FrameOutput = frameOutput,
            PipelineResourceGeneration = pipeline.ResourceGeneration,
            RendererGeneration = BackendGeneration,
            Session = _session,
            FrameSequence = _engineFrameSequence,
        };
        return true;
    }

    internal override void CompleteFrameViewHistoryCandidateAuthoring(
        in RenderFrameViewHistoryBackendReservation reservation,
        bool succeeded)
    {
        int slot = reservation.BackendSlot;
        if ((uint)slot >= (uint)_engineViewHistory.Length)
            return;
        ref EngineViewHistoryReceipt receipt = ref _engineViewHistory[slot];
        if (!receipt.Reservation.IsValid ||
            receipt.Reservation.BackendGeneration != reservation.BackendGeneration ||
            !receipt.Reservation.Candidate.MatchesIdentity(reservation.Candidate) ||
            receipt.Reservation.Output != reservation.Output ||
            !ReferenceEquals(receipt.Reservation.TargetFrameBuffer, reservation.TargetFrameBuffer))
            return;
        if (receipt.Ready)
            return;

        if (!succeeded || !IsEngineViewHistoryCurrent(in receipt, in receipt.FrameOutput) ||
            !MatchesActiveEngineViewHistory(reservation.Candidate, reservation.Output, receipt.Pipeline!, receipt.Viewport!))
        {
            SettleEngineViewHistorySlot(slot, commit: false);
            return;
        }

        // Successful authoring does not publish history. Ownership remains here
        // until the complete frame's native submission returns successfully.
        receipt.Ready = true;
    }

    /// <summary>Attests only an appended direct color draw with known positive counts.</summary>
    internal void MarkEngineViewHistoryDrawWrite(WebGpuFrameBuffer? frameBuffer,
        in RenderFrameOutputDescription output, bool hasFragmentOutput, int colorWriteMask,
        uint vertexOrIndexCount, uint instanceCount, BoundingRectangle? scissor)
    {
        if (frameBuffer is not null || _boundEngineFrameBuffer is not null ||
            !hasFragmentOutput || (colorWriteMask & 15) == 0 ||
            vertexOrIndexCount == 0 || instanceCount == 0 ||
            scissor is { Width: <= 0 } or { Height: <= 0 } ||
            output.Properties.ColorEncoding is null)
            return;
        MarkEngineViewHistoryCanvasWrite(in output);
    }

    /// <summary>Records an exact terminal write after its command entered the frame arena.</summary>
    private void MarkEngineViewHistoryCanvasWrite(in RenderFrameOutputDescription output)
    {
        if (_boundEngineFrameBuffer is not null || _engineCommandCount == 0)
            return;
        for (int index = 0; index < _engineViewHistory.Length; index++)
        {
            ref EngineViewHistoryReceipt receipt = ref _engineViewHistory[index];
            if (receipt.Ready || !IsEngineViewHistoryCurrent(in receipt, in output) ||
                !MatchesActiveEngineViewHistory(receipt.Reservation.Candidate, receipt.Reservation.Output,
                    receipt.Pipeline!, receipt.Viewport!))
                continue;
            receipt.ColorWritten = true;
            return;
        }
    }

    private static bool MatchesActiveEngineViewHistory(in RenderFrameViewHistoryCandidateToken candidate,
        in RenderOutputRequest output, XRRenderPipelineInstance pipeline, XRViewport viewport)
    {
        XRRenderPipelineInstance.RenderingState state = pipeline.RenderState;
        return ReferenceEquals(RuntimeEngine.Rendering.State.CurrentRenderingPipeline, pipeline) &&
            ReferenceEquals(state.WindowViewport, viewport) && state.OutputFBO is null &&
            !state.ShadowPass && !state.StereoPass && state.ViewHistoryAuthoring &&
            state.ViewHistoryCaptureAccepted && state.ViewHistoryCandidate.MatchesIdentity(in candidate) &&
            state.ViewHistorySequenceId == candidate.Sequence &&
            state.ViewHistorySourceFrame == candidate.SourceFrame &&
            state.ViewHistoryOutputRequest == output &&
            state.ViewHistoryPipelineIdentity == candidate.PipelineIdentity &&
            pipeline.TemporalHistoryPipelineIdentity == candidate.PipelineIdentity;
    }

    private static bool MatchesEngineViewHistoryViewport(in RenderFrameViewHistoryCandidateToken candidate,
        in RenderOutputRequest output, XRViewport viewport)
        => candidate.OutputIdentity == viewport.FrameOutputIdentity &&
            candidate.ExtentRevision == viewport.ExtentRevision && output.FrameId == candidate.SourceFrame &&
            output.Target.DisplayWidth == viewport.Width && output.Target.DisplayHeight == viewport.Height &&
            output.Target.InternalWidth == viewport.InternalWidth && output.Target.InternalHeight == viewport.InternalHeight;

    private bool IsEngineViewHistoryCurrent(in EngineViewHistoryReceipt receipt,
        in RenderFrameOutputDescription output)
        => receipt.Reservation.IsValid && State == BrowserRendererState.Ready && AcceptsBackendWork &&
            _engineRecording && ReferenceEquals(Current, this) &&
            receipt.Session == _session && receipt.RendererGeneration == BackendGeneration &&
            receipt.FrameSequence == _engineFrameSequence && receipt.FrameOutput == output &&
            CurrentFrameOutput == output && TryDescribeFrameOutput(out RenderFrameOutputDescription currentOutput) &&
            currentOutput == output && receipt.Viewport is { } viewport && receipt.Pipeline is { } pipeline &&
            ReferenceEquals(_engineViewport, viewport) && ReferenceEquals(viewport.RenderPipelineInstance, pipeline) &&
            receipt.PipelineResourceGeneration == pipeline.ResourceGeneration &&
            receipt.Reservation.Output.Target.TargetGeneration == unchecked((ulong)pipeline.ResourceGeneration) &&
            receipt.Reservation.Candidate.PipelineIdentity == pipeline.TemporalHistoryPipelineIdentity &&
            MatchesEngineViewHistoryViewport(receipt.Reservation.Candidate, receipt.Reservation.Output, viewport);

    /// <summary>Settles metadata at accepted submission, independently of GPU resource retirement.</summary>
    private void SettleSubmittedEngineViewHistory(bool presented, in RenderFrameOutputDescription output)
    {
        for (int index = 0; index < _engineViewHistory.Length; index++)
        {
            ref readonly EngineViewHistoryReceipt receipt = ref _engineViewHistory[index];
            bool commit = presented && !_engineDrawPending && receipt.Ready && receipt.ColorWritten &&
                IsEngineViewHistoryCurrent(in receipt, in output);
            SettleEngineViewHistorySlot(index, commit);
        }
    }

    private void SettleEngineViewHistorySlot(int slot, bool commit)
    {
        RenderFrameViewHistoryCandidateToken candidate = _engineViewHistory[slot].Reservation.Candidate;
        _engineViewHistory[slot] = default;
        if (commit) candidate.Commit();
        else candidate.Discard();
    }

    private void DiscardEngineViewHistory()
    {
        for (int index = 0; index < _engineViewHistory.Length; index++)
            SettleEngineViewHistorySlot(index, commit: false);
    }
}
