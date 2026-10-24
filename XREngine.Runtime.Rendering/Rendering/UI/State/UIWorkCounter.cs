namespace XREngine.Rendering.UI.State;

/// <summary>Names cumulative UI work counters. Buffer counters describe CPU commits, not device transfers.</summary>
public enum UIWorkCounter
{
    MutationsRecorded,
    AncestorsInvalidated,
    GlobalInvalidations,
    MeasureVisits,
    MeasureCacheHits,
    ArrangeVisits,
    ArrangeCacheHits,
    HitQueries,
    HitCandidates,
    GlyphCopies,
    GlyphsCopied,
    BatchEntriesCollected,
    BatchCollectionResets,
    BufferBytesCommitted,
    BufferCommitRanges,
    PublicationRecordCellsCopied,
    PublicationRecordPathsCopied,
    PublicationRecordClaimsRetained,
    PublicationRecordClaimsReleased,
    PublicationFlatCellsMaterialized,
    PublicationSegmentSlotsMaterialized,
    PublicationSegmentSlotsCopied,
    Count,
}
