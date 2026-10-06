namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>Owns one pending submission and the last accepted image identities.</summary>
internal sealed class TemporalHistorySubmissionState
{
    public ulong Epoch = 1;
    public ulong NextToken;
    public TemporalHistorySubmissionCandidate Candidate;
    public bool Staged;
    public bool Sealed;
    public bool Recorded;
    public VPRC_TemporalAccumulationPass.TemporalHistoryCoverage Coverage;
    public uint MatrixLayerMask;
    public TemporalHistoryEyeSnapshot LeftEye;
    public TemporalHistoryEyeSnapshot RightEye;
    public bool ExposureReady;
    public bool HasAcceptedResources;
    public TemporalHistoryResourceSet AcceptedResources;

    public void ClearPending()
    {
        Candidate = default;
        Staged = false;
        Sealed = false;
        Recorded = false;
        Coverage = default;
        MatrixLayerMask = 0;
        LeftEye = default;
        RightEye = default;
        ExposureReady = false;
    }
}
