namespace XREngine.Rendering;

/// <summary>
/// Immutable ownership receipt for one resolved desktop temporal-history
/// candidate. A token is valid only for its originating ledger generation.
/// </summary>
internal readonly struct RenderFrameViewHistoryCandidateToken
{
    private readonly RenderFrameViewHistoryLedger? _owner;

    internal RenderFrameViewHistoryCandidateToken(
        RenderFrameViewHistoryLedger owner,
        ulong ledgerGeneration,
        ulong candidateId,
        ulong sequence,
        ulong sourceFrame,
        ulong pipelineIdentity,
        ulong extentRevision,
        ulong outputIdentity)
    {
        _owner = owner;
        LedgerGeneration = ledgerGeneration;
        CandidateId = candidateId;
        Sequence = sequence;
        SourceFrame = sourceFrame;
        PipelineIdentity = pipelineIdentity;
        ExtentRevision = extentRevision;
        OutputIdentity = outputIdentity;
    }

    /// <summary>Gets whether this receipt identifies a live ledger candidate.</summary>
    internal bool IsValid => _owner is not null && LedgerGeneration != 0UL && CandidateId != 0UL;
    internal bool IsOwnedBy(RenderFrameViewHistoryLedger owner) => ReferenceEquals(_owner, owner);
    internal bool MatchesIdentity(in RenderFrameViewHistoryCandidateToken other)
        => ReferenceEquals(_owner, other._owner) &&
            LedgerGeneration == other.LedgerGeneration &&
            CandidateId == other.CandidateId &&
            Sequence == other.Sequence &&
            SourceFrame == other.SourceFrame &&
            PipelineIdentity == other.PipelineIdentity &&
            ExtentRevision == other.ExtentRevision &&
            OutputIdentity == other.OutputIdentity;
    internal bool SharesLedger(in RenderFrameViewHistoryCandidateToken other)
        => ReferenceEquals(_owner, other._owner) && LedgerGeneration == other.LedgerGeneration;
    internal ulong LedgerGeneration { get; }
    internal ulong CandidateId { get; }
    internal ulong Sequence { get; }
    internal ulong SourceFrame { get; }
    internal ulong PipelineIdentity { get; }
    internal ulong ExtentRevision { get; }
    internal ulong OutputIdentity { get; }

    /// <summary>Publishes the frozen candidate when its exact ledger slot remains live.</summary>
    internal void Commit() => _owner?.Commit(in this);

    /// <summary>Releases only this exact candidate when its ledger slot remains live.</summary>
    internal void Discard() => _owner?.Discard(in this);
}
