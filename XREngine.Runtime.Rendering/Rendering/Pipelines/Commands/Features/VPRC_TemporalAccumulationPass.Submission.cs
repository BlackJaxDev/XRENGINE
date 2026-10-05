using XREngine.Data.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Pipelines.Commands;

public sealed partial class VPRC_TemporalAccumulationPass
{
    [ThreadStatic] private static XRRenderPipelineInstance? s_strictCapturePipeline;
    [ThreadStatic] private static TemporalState? s_strictCaptureState;
    [ThreadStatic] private static ulong s_strictCaptureSerial;
    [ThreadStatic] private static ulong s_strictCaptureToken;
    [ThreadStatic] private static bool s_strictCaptureTransferred;
    [ThreadStatic] private static bool s_strictCaptureBlocked;
    private static long s_submissionDiagnosticFailures;

    internal static long SubmissionDiagnosticFailures => Interlocked.Read(ref s_submissionDiagnosticFailures);

    /// <summary>Defers history publication for this explicit backend authoring scope.</summary>
    internal static TemporalHistoryCaptureScope EnterStrictSpsCapture(XRRenderPipelineInstance instance)
    {
        if (s_strictCapturePipeline is not null)
            throw new InvalidOperationException("Temporal submission capture scopes cannot overlap on one thread.");

        s_strictCapturePipeline = instance;
        s_strictCaptureState = null;
        s_strictCaptureToken = 0;
        s_strictCaptureTransferred = false;
        s_strictCaptureBlocked = false;
        s_strictCaptureSerial = NextSubmissionIdentity(s_strictCaptureSerial);
        return new(instance, s_strictCaptureSerial);
    }

    internal static void EndStrictSpsCapture(XRRenderPipelineInstance instance, ulong serial)
    {
        if (!ReferenceEquals(s_strictCapturePipeline, instance) || serial != s_strictCaptureSerial)
            return;

        TemporalState? state = s_strictCaptureState;
        ulong token = s_strictCaptureToken;
        bool transferred = s_strictCaptureTransferred;
        s_strictCapturePipeline = null;
        s_strictCaptureState = null;
        s_strictCaptureToken = 0;
        s_strictCaptureTransferred = false;
        s_strictCaptureBlocked = false;
        if (state is null || transferred)
            return;

        lock (state.MutationSync)
        {
            if (state.Submission.Candidate.Token != token)
                return;
            try
            {
                try
                {
                    state.ActiveJitterHandle?.Dispose();
                }
                finally
                {
                    state.ActiveRightEyeJitterHandle?.Dispose();
                }
            }
            finally
            {
                state.ActiveJitterHandle = null;
                state.ActiveRightEyeJitterHandle = null;
                state.Submission.ClearPending();
                state.PendingHistoryReady = false;
                state.PendingHistoryCoverage.Clear();
            }
        }
    }

    internal static bool HasStrictHistoryAttempt(XRRenderPipelineInstance instance, ulong serial)
        => ReferenceEquals(s_strictCapturePipeline, instance) && serial == s_strictCaptureSerial &&
           (s_strictCaptureState is not null || s_strictCaptureBlocked);

    private static bool TryBeginStrictHistoryCapture(XRRenderPipelineInstance instance, TemporalState state)
    {
        if (!ReferenceEquals(s_strictCapturePipeline, instance))
            return true;

        TemporalHistorySubmissionState submission = state.Submission;
        if (submission.Candidate.IsValid)
        {
            s_strictCaptureBlocked = true;
            return false;
        }

        submission.NextToken = NextSubmissionIdentity(submission.NextToken);
        submission.Candidate = new(state, submission.Epoch, submission.NextToken)
        {
            PipelineInstance = instance,
        };
        s_strictCaptureState = state;
        s_strictCaptureToken = submission.NextToken;
        return true;
    }

    private static void BindStrictHistoryBegin(XRRenderPipelineInstance instance, TemporalState state)
    {
        if (!ReferenceEquals(s_strictCaptureState, state))
            return;

        if (!ShouldPopulateTemporalInput(state.LastAntiAliasingMode) ||
            IsHistoryIsolationPolicyDisabled(state.HistoryIsolationPolicy))
        {
            state.Submission.ClearPending();
            s_strictCaptureState = null;
            s_strictCaptureToken = 0;
            return;
        }

        TemporalHistoryGenerationTracker generation = state.HistoryGeneration;
        state.Submission.Candidate = state.Submission.Candidate with
        {
            Epoch = state.Submission.Epoch,
            RenderFrameId = RuntimeEngine.Rendering.State.RenderFrameId,
            ProfileGeneration = generation.ProfileGeneration,
            LeftEyeResetGeneration = generation.LeftEyeResetGeneration,
            RightEyeResetGeneration = generation.RightEyeResetGeneration,
            ExpectedLayerMask = generation.ExpectedLayerMask,
            AuthoredHistoryReady = state.HistoryReady,
            RequiresTsrColor = state.PendingHistoryCoverage.RequiresTsrColor,
        };
    }

    private void RegisterStrictHistoryTargets(XRRenderPipelineInstance instance)
    {
        TemporalState? state = s_strictCaptureState;
        if (state is null || !ReferenceEquals(s_strictCapturePipeline, instance))
            return;

        if (Phase == EPhase.Accumulate)
        {
            XRFrameBuffer? history = instance.GetFBO<XRFrameBuffer>(HistoryColorFBOName);
            bool internalAccumulation = ShouldRunInternalAccumulation(ResolveAntiAliasingMode());
            XRFrameBuffer? exposure = internalAccumulation
                ? instance.GetFBO<XRFrameBuffer>(HistoryExposureFBOName) : null;
            XRTexture? depthView = instance.GetTexture<XRTexture>(HistoryDepthViewTextureName);
            lock (state.MutationSync)
                state.Submission.Candidate = state.Submission.Candidate with
                {
                    ColorHistoryTarget = history,
                    DepthHistoryTarget = history,
                    ColorReadTexture = internalAccumulation ? GetHistoryColorTexture(history) : null,
                    DepthReadTexture = depthView,
                    ExposureHistoryTarget = exposure,
                    ExposureReadTexture = GetHistoryColorTexture(exposure),
                    TemporalResolveTarget = internalAccumulation
                        ? instance.GetFBO<XRFrameBuffer>(TemporalAccumulationFBOName) : null,
                };
        }
        else
        {
            XRFrameBuffer? history = instance.GetFBO<XRFrameBuffer>(TsrHistoryColorFBOName);
            XRFrameBuffer? metadata = TsrHistoryMetadataFBOName is not null
                ? instance.GetFBO<XRFrameBuffer>(TsrHistoryMetadataFBOName) : null;
            lock (state.MutationSync)
                state.Submission.Candidate = state.Submission.Candidate with
                {
                    TsrColorHistoryTarget = history,
                    TsrColorReadTexture = GetHistoryColorTexture(history),
                    MetadataHistoryTarget = metadata,
                    MetadataReadTexture = GetHistoryColorTexture(metadata),
                    TsrResolveTarget = TsrResolveQuadFBOName is not null
                        ? instance.GetFBO<XRFrameBuffer>(TsrResolveQuadFBOName) : null,
                };
        }
    }

    private static XRTexture? GetHistoryColorTexture(XRFrameBuffer? frameBuffer)
    {
        if (frameBuffer?.Targets is not { } targets)
            return null;
        for (int i = 0; i < targets.Length; i++)
            if (targets[i].Attachment == EFrameBufferAttachment.ColorAttachment0)
                return targets[i].Target as XRTexture;
        return null;
    }

    private static bool TryStageStrictHistory(XRRenderPipelineInstance instance, TemporalState state)
    {
        if (!ReferenceEquals(s_strictCapturePipeline, instance))
            return false;

        TemporalHistorySubmissionState submission = state.Submission;
        if (!ReferenceEquals(s_strictCaptureState, state) ||
            !IsCurrentSubmission(state, in submission.Candidate) ||
            IsHistoryIsolationPolicyDisabled(state.HistoryIsolationPolicy))
            return true;

        submission.Coverage = state.PendingHistoryCoverage;
        submission.MatrixLayerMask = state.HistoryGeneration.CurrentMatrixLayerMask;
        submission.LeftEye = CaptureSubmissionEye(state.LeftEye);
        submission.RightEye = CaptureSubmissionEye(state.RightEye);
        submission.Candidate = submission.Candidate with
        {
            CompleteLayerMask = submission.Coverage.CompleteLayerMask & submission.MatrixLayerMask,
        };
        submission.Staged = true;
        state.PendingHistoryReady = false;
        return true;
    }

    /// <summary>Transfers the staged attempt from the authoring scope to its recorder.</summary>
    internal static bool TryGetStagedCandidate(XRRenderPipelineInstance instance,
        out TemporalHistorySubmissionCandidate candidate)
    {
        candidate = default;
        TemporalState? state = s_strictCaptureState;
        if (!ReferenceEquals(s_strictCapturePipeline, instance) || state is null || s_strictCaptureTransferred)
            return false;

        lock (state.MutationSync)
        {
            TemporalHistorySubmissionState submission = state.Submission;
            if (!submission.Staged || !IsCurrentSubmission(state, in submission.Candidate) ||
                submission.Candidate.ExpectedLayerMask == 0 ||
                submission.Candidate.CompleteLayerMask != submission.Candidate.ExpectedLayerMask)
                return false;
            candidate = submission.Candidate;
            s_strictCaptureTransferred = true;
            return true;
        }
    }

    /// <summary>Checks physical histories before the backend prepares immutable bindings.</summary>
    internal static bool TrySealCandidate(in TemporalHistorySubmissionCandidate candidate,
        in TemporalHistoryResourceSet reads, in TemporalHistoryResourceSet writes,
        out TemporalHistorySubmissionCandidate sealedCandidate, out bool requiresReauthor)
    {
        sealedCandidate = default;
        requiresReauthor = false;
        if (candidate.Owner is not TemporalState state)
            return false;

        lock (state.MutationSync)
        {
            TemporalHistorySubmissionState submission = state.Submission;
            if (!submission.Staged || !IsCurrentSubmission(state, in candidate) ||
                !ValidateResourceRoles(in candidate, in reads, in writes))
                return false;

            bool compatibleSeed = submission.HasAcceptedResources &&
                MatchesRequiredWrites(in candidate, in submission.AcceptedResources, in writes) &&
                ContainsRequiredReads(in candidate, in submission.AcceptedResources, in reads);
            if (!compatibleSeed && candidate.AuthoredHistoryReady)
            {
                InvalidatePhysicalHistory(state);
                requiresReauthor = true;
                return false;
            }

            sealedCandidate = candidate with { ReadResources = reads, WriteResources = writes };
            submission.Candidate = sealedCandidate;
            submission.Sealed = true;
            return true;
        }
    }

    /// <summary>Accepts complete native write coverage after the whole primary records.</summary>
    internal static bool TryConfirmRecordedCandidate(in TemporalHistorySubmissionCandidate candidate,
        uint colorLayerMask, uint depthLayerMask, uint tsrColorLayerMask,
        uint metadataLayerMask, uint exposureLayerMask,
        bool temporalResolveRecorded, bool tsrResolveRecorded,
        out TemporalHistorySubmissionCandidate recordedCandidate)
    {
        recordedCandidate = default;
        if (candidate.Owner is not TemporalState state)
            return false;
        lock (state.MutationSync)
        {
            TemporalHistorySubmissionState submission = state.Submission;
            uint mask = candidate.ExpectedLayerMask;
            if (!submission.Sealed || !IsCurrentSubmission(state, in candidate) ||
                mask == 0 || (colorLayerMask & mask) != mask || (depthLayerMask & mask) != mask ||
                (candidate.RequiresTsrColor && (tsrColorLayerMask & mask) != mask) ||
                (candidate.MetadataHistoryTarget is not null && (metadataLayerMask & mask) != mask) ||
                (candidate.ExposureHistoryTarget is not null && (exposureLayerMask & mask) != mask) ||
                (candidate.TemporalResolveTarget is not null && !temporalResolveRecorded) ||
                (candidate.RequiresTsrColor && !tsrResolveRecorded) ||
                !MatchesRequiredWrites(in candidate, submission.Candidate.WriteResources, candidate.WriteResources))
                return false;
            submission.Recorded = true;
            recordedCandidate = submission.Candidate;
            return true;
        }
    }

    /// <summary>Publishes one current candidate after its native queue submission succeeds.</summary>
    internal static bool AcceptCandidate(in TemporalHistorySubmissionCandidate candidate)
    {
        if (candidate.Owner is not TemporalState state)
            return false;
        OpenXrSmokeTemporalStateLedgerEntry leftEntry = default;
        OpenXrSmokeTemporalStateLedgerEntry rightEntry = default;
        bool recordDiagnostics = Phase524bTemporalStateDiagnostics.Enabled;
        bool recordRight = false;
        lock (state.MutationSync)
        {
            TemporalHistorySubmissionState submission = state.Submission;
            if (!submission.Recorded || !IsCurrentSubmission(state, in candidate) ||
                !MatchesRequiredWrites(in candidate, submission.Candidate.WriteResources, candidate.WriteResources))
            {
                ReleaseRejectedSubmission(state, in candidate);
                return false;
            }

            TemporalHistoryGenerationTracker generation = state.HistoryGeneration;
            bool wasReady = state.HistoryReady;
            bool leftWasReady = generation.LeftEyeHistoryReady;
            bool rightWasReady = generation.RightEyeHistoryReady;
            ulong leftPrevious = ComputeMatrixFingerprint(state.LeftEye.PrevViewProjectionUnjittered);
            ulong rightPrevious = ComputeMatrixFingerprint(state.RightEye.PrevViewProjectionUnjittered);
            ulong leftCurrent = ComputeMatrixFingerprint(submission.LeftEye.ViewProjectionUnjittered);
            ulong rightCurrent = ComputeMatrixFingerprint(submission.RightEye.ViewProjectionUnjittered);
            uint committed = generation.CommitCapturedFrame(in submission.Coverage, submission.MatrixLayerMask);
            if ((committed & 1u) != 0)
                CommitSubmissionEye(state.LeftEye, in submission.LeftEye);
            if ((committed & 2u) != 0)
                CommitSubmissionEye(state.RightEye, in submission.RightEye);
            state.HistoryReady = generation.HistoryReady;
            state.HistoryExposureReady = state.HistoryReady && candidate.ExposureHistoryTarget is not null && submission.ExposureReady;
            submission.AcceptedResources = candidate.WriteResources;
            submission.HasAcceptedResources = committed == candidate.ExpectedLayerMask;
            state.PendingHistoryReady = false;
            PublishTemporalUniformData(state);
            if (recordDiagnostics)
            {
                leftEntry = CreateTemporalEyeStateEvidence(candidate.RenderFrameId, candidate.PipelineInstance!.InstanceId,
                    state, generation, 0, committed, wasReady, leftWasReady, generation.LeftEyeHistoryReady,
                    leftPrevious, leftCurrent);
                recordRight = (candidate.ExpectedLayerMask & 2u) != 0;
                if (recordRight)
                    rightEntry = CreateTemporalEyeStateEvidence(candidate.RenderFrameId, candidate.PipelineInstance.InstanceId,
                        state, generation, 1, committed, wasReady, rightWasReady, generation.RightEyeHistoryReady,
                        rightPrevious, rightCurrent);
            }
            submission.ClearPending();
            state.PendingHistoryCoverage.Clear();
        }
        if (recordDiagnostics)
        {
            try
            {
                Phase524bTemporalStateDiagnostics.Record(in leftEntry);
                if (recordRight)
                    Phase524bTemporalStateDiagnostics.Record(in rightEntry);
            }
            catch
            {
                // Diagnostic failure cannot revoke native submission acceptance.
                Interlocked.Increment(ref s_submissionDiagnosticFailures);
            }
        }
        return true;
    }

    /// <summary>Releases only this rejected attempt. A newer candidate is left unchanged.</summary>
    internal static void DiscardCandidate(in TemporalHistorySubmissionCandidate candidate)
    {
        if (candidate.Owner is not TemporalState state)
            return;
        lock (state.MutationSync)
            ReleaseRejectedSubmission(state, in candidate);
    }

    /// <summary>Invalidates history when this attempt belongs to a lost backend device.</summary>
    internal static void InvalidateCandidateForDeviceLoss(in TemporalHistorySubmissionCandidate candidate)
    {
        if (candidate.Owner is not TemporalState state || !candidate.IsValid)
            return;
        lock (state.MutationSync)
        {
            TemporalHistorySubmissionState submission = state.Submission;
            object? backendOwner = candidate.WriteResources.Color.BackendOwner;
            bool ownsPendingAttempt = submission.Candidate.Token == candidate.Token;
            bool ownsAcceptedHistory = submission.HasAcceptedResources && backendOwner is not null &&
                ReferenceEquals(submission.AcceptedResources.Color.BackendOwner, backendOwner);
            if (!ownsPendingAttempt && !ownsAcceptedHistory)
                return;
            InvalidatePhysicalHistory(state);
            ReleaseRejectedSubmission(state, in candidate);
        }
    }

    private static void ReleaseRejectedSubmission(TemporalState state, in TemporalHistorySubmissionCandidate candidate)
    {
        if (!candidate.IsValid || state.Submission.Candidate.Token != candidate.Token)
            return;
        state.Submission.ClearPending();
        state.PendingHistoryReady = false;
        state.PendingHistoryCoverage.Clear();
    }

    private static bool IsCurrentSubmission(TemporalState state, in TemporalHistorySubmissionCandidate candidate)
        => candidate.IsValid && ReferenceEquals(candidate.Owner, state) &&
           candidate.Token == state.Submission.Candidate.Token && candidate.Epoch == state.Submission.Epoch &&
           candidate.Epoch == state.Submission.Candidate.Epoch &&
           candidate.ProfileGeneration == state.HistoryGeneration.ProfileGeneration &&
           candidate.LeftEyeResetGeneration == state.HistoryGeneration.LeftEyeResetGeneration &&
           candidate.RightEyeResetGeneration == state.HistoryGeneration.RightEyeResetGeneration;

    private static void InvalidateSubmissionHistory(TemporalState state)
    {
        state.Submission.Epoch = NextSubmissionIdentity(state.Submission.Epoch);
        state.Submission.HasAcceptedResources = false;
        state.Submission.AcceptedResources = default;
    }

    private static void InvalidatePhysicalHistory(TemporalState state)
    {
        InvalidateSubmissionHistory(state);
        state.HistoryGeneration.InvalidateLayers(state.HistoryGeneration.ExpectedLayerMask);
        state.LeftEye.ResetHistory();
        state.RightEye.ResetHistory();
        state.HistoryReady = false;
        state.HistoryExposureReady = false;
        PublishTemporalUniformData(state, captureResolve: true);
    }

    private static bool ValidateResourceRoles(in TemporalHistorySubmissionCandidate candidate,
        in TemporalHistoryResourceSet reads, in TemporalHistoryResourceSet writes)
        => candidate.ColorHistoryTarget is not null && candidate.DepthHistoryTarget is not null &&
           writes.Color.IsValid && writes.Depth.IsValid &&
           candidate.DepthReadTexture is not null &&
           (!candidate.RequiresTsrColor || (candidate.TsrColorHistoryTarget is not null &&
               candidate.TsrColorReadTexture is not null && candidate.TsrResolveTarget is not null &&
               writes.TsrColor.IsValid)) &&
           (candidate.MetadataHistoryTarget is null || (candidate.MetadataReadTexture is not null && writes.Metadata.IsValid)) &&
           (candidate.ExposureHistoryTarget is null || (candidate.ColorReadTexture is not null &&
               candidate.ExposureReadTexture is not null && candidate.TemporalResolveTarget is not null &&
               writes.Exposure.IsValid)) &&
           ContainsRequiredReads(in candidate, in writes, in reads);

    private static bool ContainsRequiredReads(in TemporalHistorySubmissionCandidate candidate,
        in TemporalHistoryResourceSet writes, in TemporalHistoryResourceSet reads)
        => (candidate.ColorReadTexture is null || writes.Color.Contains(reads.Color)) &&
           (candidate.DepthReadTexture is null || writes.Depth.Contains(reads.Depth)) &&
           (candidate.TsrColorReadTexture is null || writes.TsrColor.Contains(reads.TsrColor)) &&
           (candidate.MetadataReadTexture is null || writes.Metadata.Contains(reads.Metadata)) &&
           (candidate.ExposureReadTexture is null || writes.Exposure.Contains(reads.Exposure));

    private static bool MatchesRequiredWrites(in TemporalHistorySubmissionCandidate candidate,
        in TemporalHistoryResourceSet first, in TemporalHistoryResourceSet second)
        => first.Color.Matches(second.Color) && first.Depth.Matches(second.Depth) &&
           (!candidate.RequiresTsrColor || first.TsrColor.Matches(second.TsrColor)) &&
           (candidate.MetadataHistoryTarget is null || first.Metadata.Matches(second.Metadata)) &&
           (candidate.ExposureHistoryTarget is null || first.Exposure.Matches(second.Exposure));

    private static TemporalHistoryEyeSnapshot CaptureSubmissionEye(TemporalEyeState eye)
        => new(eye.CurrentJitter, eye.CurrDepthZeroToOne, eye.CurrReversedDepth,
            eye.CurrViewMatrix, eye.CurrProjection, eye.CurrViewProjection,
            eye.CurrViewProjectionUnjittered, eye.CurrInverseViewProjection,
            eye.CurrentCameraPosition, eye.CurrentCameraForward);

    private static void CommitSubmissionEye(TemporalEyeState eye, in TemporalHistoryEyeSnapshot snapshot)
    {
        eye.PreviousJitter = snapshot.Jitter;
        eye.PrevDepthZeroToOne = snapshot.DepthZeroToOne;
        eye.PrevReversedDepth = snapshot.ReversedDepth;
        eye.PrevViewMatrix = snapshot.ViewMatrix;
        eye.PrevProjection = snapshot.Projection;
        eye.PrevViewProjection = snapshot.ViewProjection;
        eye.PrevViewProjectionUnjittered = snapshot.ViewProjectionUnjittered;
        eye.PrevInverseViewProjection = snapshot.InverseViewProjection;
        eye.PreviousCameraPosition = snapshot.CameraPosition;
        eye.PreviousCameraForward = snapshot.CameraForward;
    }

    private static ulong NextSubmissionIdentity(ulong value) => value == ulong.MaxValue ? 1 : value + 1;
}
