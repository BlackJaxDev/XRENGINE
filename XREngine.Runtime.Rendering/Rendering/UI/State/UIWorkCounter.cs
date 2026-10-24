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
    /// <summary>Tree nodes visited by publication capture walks.</summary>
    PublicationCaptureNodesVisited,
    /// <summary>Captures that rebuilt every record from a full tree walk.</summary>
    PublicationFullCaptures,
    /// <summary>Captures that walked only changed subtrees and patched record pages.</summary>
    PublicationSparseCaptures,
    /// <summary>Sparse captures that changed record counts and copied clean records into new lists.</summary>
    PublicationSparseSplices,
    /// <summary>Sparse attempts that could not capture their changed subtrees and used a full capture.</summary>
    PublicationSparseFallbacks,
    /// <summary>Text layouts that reused a cached result for equal glyph-placement inputs.</summary>
    TextLayoutCacheHits,
    /// <summary>Text layouts that found no cached result and placed glyphs again.</summary>
    TextLayoutCacheMisses,
    /// <summary>Native UI failures reported through <see cref="UIFailureDiagnostics"/>.</summary>
    FailuresReported,
    /// <summary>Direct-world UI draws that used the route without checked indexed-draw proof.</summary>
    WorldUncheckedDraws,
    Count,
}
