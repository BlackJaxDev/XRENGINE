using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.Components;

/// <summary>
/// Sparse publication capture. A full capture records, for each layout node, the live
/// transform, the inherited capture context, and where its records start. A later capture
/// with the same layout topology reads committed journal changes and changed layout pages,
/// and walks only the changed subtrees. When record counts stay the same, it replaces only
/// the record pages that differ. When they change, it splices the recaptured ranges into the
/// previous stores, which share every page that no edit touches.
/// </summary>
public partial class UICanvasComponent
{
    private static readonly Comparison<(int Index, UIRenderRecord Old, UIRenderRecord New)> RenderPatchOrder =
        static (left, right) => left.Index.CompareTo(right.Index);
    private static readonly Comparison<(int Position, UIRenderRecord Record)> PainterPositionOrder =
        static (left, right) => left.Position.CompareTo(right.Position);

    // Walk state that describes _sparseBasis. It is valid only while _sparseBasis is set.
    private UIPublication? _sparseBasis;
    private UICommittedChangeCursor _sparseCursor;
    private bool _sparseOrderValid;
    private int _sparseExpectedIndex;
    private UITransform?[] _sparseTransforms = [];
    private UIPublicationCaptureContext[] _sparseContexts = [];
    private int[] _sparseSubtreeEnds = [];
    private int[] _sparseHiddenRoots = [];
    private int[] _sparseHitStarts = [];
    private int[] _sparseScrollStarts = [];
    private int[] _sparseScopeStarts = [];
    private int[] _sparseRenderStarts = [];
    private long[] _sparseRenderMemberships = [];
    private long[] _sparseSpareMemberships = [];
    private int[] _sparseMarks = [];
    private int _sparseMarkStamp;
    private int _sparseHitBase;
    private int _sparseScrollBase;
    private int _sparseScopeBase;
    private int _sparseRenderBase;
    private UIElementChange[] _sparseChanges = [];
    private readonly List<int> _sparseRoots = [];
    private readonly List<(UIElementIdentity Dependency, int Dependent)> _sparseDependencies = [];

    // Patch data for the publication that the current capture builds.
    private readonly List<(int Index, UIHitRecord Record)> _sparseHitPatches = [];
    private readonly List<(int Index, UIRenderRecord Old, UIRenderRecord New)> _sparseRenderPatches = [];
    private UIScrollRecord[]? _sparseScrollReplacement;
    private UIFocusScopeRecord[]? _sparseScopeReplacement;
    private bool _sparseSpliced;
    private readonly List<UIPublicationSparseRange> _sparseRanges = [];
    private readonly List<UIHitRecord> _sparseNewHits = [];
    private readonly List<UIScrollRecord> _sparseNewScroll = [];
    private readonly List<UIFocusScopeRecord> _sparseNewScopes = [];
    private readonly List<UIRenderRecord> _sparseNewRenders = [];

    // Splice data for a capture that changed record counts.
    private readonly List<UIRecordSplice> _sparseHitSplices = [];
    private readonly List<UIRecordSplice> _sparseRenderSplices = [];
    private readonly List<UIRenderRecord> _sparseSplicedRenders = [];
    private readonly List<int> _sparseRemovedRenders = [];
    private readonly List<(int Position, UIRenderRecord Record)> _sparseInsertedRenders = [];
    private int _sparseBackdropDelta;
    private int _sparsePreRenderDelta;

    /// <summary>Rebuilds the identity lookup only when layout publishes a new identity array.</summary>
    private void EnsurePublicationLayoutIndices(ReadOnlyMemory<UIElementIdentity> identities)
    {
        object? source = MemoryMarshal.TryGetArray(identities, out ArraySegment<UIElementIdentity> segment) &&
            segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? segment.Array : null;
        if (source is not null && ReferenceEquals(source, _publicationLayoutIndexSource))
            return;
        _publicationLayoutIndexSource = null;
        _publicationLayoutIndices.Clear();
        for (int index = 0; index < identities.Length; index++)
            _publicationLayoutIndices.Add(identities.Span[index], index);
        _publicationLayoutIndexSource = source;
    }

    private void CaptureFull(UICanvasTransform canvas, UILayoutOutputPageView layoutPages,
        IReadOnlyList<long> painterOrder, int nodeCount)
    {
        _sparseBasis = null;
        UIRuntimeCounters.Add(UIWorkCounter.PublicationFullCaptures);
        EnsureSparseCapacity(nodeCount);
        _sparseOrderValid = _publicationLayoutIndexSource is not null;
        _sparseExpectedIndex = 0;
        CaptureSubtreeEnds(canvas);
        if (_sparseExpectedIndex != nodeCount)
            _sparseOrderValid = false;
        ResetSparseStarts(0, nodeCount);
        _sparseDependencies.Clear();
        SetSparseWalkBases(0, 0, 0, 0);
        _publicationHitScratch.Clear();
        _publicationScrollScratch.Clear();
        _publicationFocusScopeScratch.Clear();
        _publicationRenderScratch.Clear();
        CaptureHitRecords(canvas, canvas, layoutPages, default);
        _sparseHitStarts[nodeCount] = _publicationHitScratch.Count;
        _sparseScrollStarts[nodeCount] = _publicationScrollScratch.Count;
        _sparseScopeStarts[nodeCount] = _publicationFocusScopeScratch.Count;
        _sparseRenderStarts[nodeCount] = _publicationRenderScratch.Count;
        if (nodeCount == 0 || _sparseHitStarts[0] < 0)
            _sparseOrderValid = false;
        else
            FixSparseStarts(0, nodeCount);
        if (_sparseRenderMemberships.Length < _publicationRenderScratch.Count)
            _sparseRenderMemberships = new long[_publicationRenderScratch.Count];
        for (int index = 0; index < _publicationRenderScratch.Count; index++)
            _sparseRenderMemberships[index] = _publicationRenderScratch[index].MembershipId;
        OrderPublicationRenders(painterOrder);
        OrderPublicationFocusScopes();
    }

    /// <summary>
    /// Captures only changed subtrees. Returns false when the basis is missing or the change
    /// alters layout topology; the caller then performs a full capture. When record counts
    /// change, the result is spliced into complete record lists without walking clean nodes.
    /// </summary>
    private bool TryCaptureSparse(UIPublication previous, UICanvasTransform canvas,
        ReadOnlyMemory<UIElementIdentity> identities, UILayoutOutputPageView layoutPages,
        UICommittedChangeCursor cursor, bool painterOrderChanged, long worldCollectionRevision,
        Matrix4x4 canvasWorldMatrix, ECanvasDrawSpace drawSpace, bool offscreen,
        BoundingRectangleF canvasBounds, UIRenderPainterIndex painterIndex, bool directWorld)
    {
        int nodeCount = identities.Length;
        if (!ReferenceEquals(previous, _sparseBasis) || !_sparseOrderValid || painterOrderChanged ||
            nodeCount == 0 || _publicationLayoutIndexSource is null ||
            previous.CanvasLifetime != (ulong)Volatile.Read(ref _canvasLifetime) ||
            !identities.Equals(previous.Identities) ||
            previous.LayoutPages is not { } previousPages || previousPages.Length != nodeCount ||
            previous.WorldCollectionRevision != worldCollectionRevision ||
            previous.AuthoringCanvasWorldMatrix != canvasWorldMatrix ||
            previous.AuthoringDrawSpace != drawSpace || previous.AuthoringOffscreen != offscreen ||
            previous.CanvasBounds != canvasBounds || _sparseCursor.JournalId != cursor.JournalId)
            return false;

        if (!TryCollectSparseRoots(canvas.ChangeJournal, cursor, layoutPages, previousPages,
                nodeCount))
            return false;

        // Recapture changes the walk state below; it describes no publication until acceptance.
        _sparseBasis = null;
        _sparseSpliced = false;
        _sparseRanges.Clear();
        _sparseNewHits.Clear();
        _sparseNewScroll.Clear();
        _sparseNewScopes.Clear();
        _sparseNewRenders.Clear();
        int hitDelta = 0;
        int scrollDelta = 0;
        int scopeDelta = 0;
        int renderDelta = 0;
        for (int rootIndex = 0; rootIndex < _sparseRoots.Count; rootIndex++)
            if (!TryCaptureSparseRoot(canvas, identities.Span, layoutPages,
                    _sparseRoots[rootIndex], directWorld, ref hitDelta, ref scrollDelta,
                    ref scopeDelta, ref renderDelta))
            {
                UIRuntimeCounters.Add(UIWorkCounter.PublicationSparseFallbacks);
                return false;
            }

        if (KeepsSparsePlacement(previous))
            BuildSparsePatches(previous);
        else
        {
            // The previous records follow this painter order, because it did not change.
            if (!TryBuildRenderSplices(previous, painterIndex))
            {
                UIRuntimeCounters.Add(UIWorkCounter.PublicationSparseFallbacks);
                return false;
            }
            SpliceSparseRecords(previous, nodeCount, hitDelta, scrollDelta, scopeDelta, renderDelta);
            _sparseSpliced = true;
            UIRuntimeCounters.Add(UIWorkCounter.PublicationSparseSplices);
        }
        UIRuntimeCounters.Add(UIWorkCounter.PublicationSparseCaptures);
        return true;
    }

    private bool TryCollectSparseRoots(UIChangeJournal journal, UICommittedChangeCursor cursor,
        UILayoutOutputPageView layoutPages, UILayoutOutputPageView previousPages, int nodeCount)
    {
        if (_sparseChanges.Length < journal.Capacity)
            _sparseChanges = new UIElementChange[journal.Capacity];
        if (!journal.TryCopyCommittedSince(_sparseCursor, _sparseChanges,
                out UICommittedChangeRead read) || read.EndCursor != cursor)
            return false;

        int stamp = NextSparseStamp();
        _sparseRoots.Clear();
        for (int index = 0; index < read.Count; index++)
        {
            UIElementChange change = _sparseChanges[index];
            // A removal or an element outside this layout changes record placement.
            if (change.IsRemoved ||
                !_publicationLayoutIndices.TryGetValue(change.Element, out int nodeIndex))
                return false;
            AddSparseRoot(nodeIndex, stamp);
        }
        if (!ReferenceEquals(layoutPages, previousPages))
            for (int start = 0; start < nodeCount; start += UILayoutOutputPageView.PageSize)
            {
                // The layout builder shares every page that it did not edit.
                if (layoutPages.SharesPageWith(previousPages, start))
                    continue;
                int end = Math.Min(nodeCount, start + UILayoutOutputPageView.PageSize);
                for (int nodeIndex = start; nodeIndex < end; nodeIndex++)
                    if (!SameLayoutOutput(in layoutPages[nodeIndex], in previousPages[nodeIndex]))
                        AddSparseRoot(nodeIndex, stamp);
            }

        NormalizeSparseRoots();
        // Pointer geometry reads other nodes, such as a scroll target or a split target.
        bool added;
        do
        {
            added = false;
            for (int index = 0; index < _sparseDependencies.Count; index++)
            {
                (UIElementIdentity dependency, int dependent) = _sparseDependencies[index];
                if (_publicationLayoutIndices.TryGetValue(dependency, out int dependencyIndex) &&
                    IsCoveredBySparseRoot(dependencyIndex) && !IsCoveredBySparseRoot(dependent) &&
                    AddSparseRoot(dependent, stamp))
                    added = true;
            }
            if (added)
                NormalizeSparseRoots();
        } while (added);

        if (_sparseRoots.Count > 0 && _sparseRoots[0] == 0)
            return false;
        long covered = 0;
        for (int index = 0; index < _sparseRoots.Count; index++)
            covered += _sparseSubtreeEnds[_sparseRoots[index]] - _sparseRoots[index];
        // A change that covers most of the tree costs less as one full walk.
        return covered * 2 < nodeCount;
    }

    private bool TryCaptureSparseRoot(UICanvasTransform canvas,
        ReadOnlySpan<UIElementIdentity> identities, UILayoutOutputPageView layoutPages,
        int root, bool directWorld, ref int hitDelta, ref int scrollDelta, ref int scopeDelta,
        ref int renderDelta)
    {
        int end = _sparseSubtreeEnds[root];
        if (_sparseTransforms[root] is not { IsDestroyed: false } transform ||
            transform.ElementIdentity != identities[root])
            return false;
        // Starts outside recaptured subtrees keep old coordinates until a splice shifts them.
        int oldHitStart = _sparseHitStarts[root];
        int oldScrollStart = _sparseScrollStarts[root];
        int oldScopeStart = _sparseScopeStarts[root];
        int oldRenderStart = _sparseRenderStarts[root];
        int oldHitCount = _sparseHitStarts[end] - oldHitStart;
        int oldScrollCount = _sparseScrollStarts[end] - oldScrollStart;
        int oldScopeCount = _sparseScopeStarts[end] - oldScopeStart;
        int oldRenderCount = _sparseRenderStarts[end] - oldRenderStart;

        RemoveSparseDependencies(root, end);
        ResetSparseStarts(root, end);
        SetSparseWalkBases(oldHitStart + hitDelta, oldScrollStart + scrollDelta,
            oldScopeStart + scopeDelta, oldRenderStart + renderDelta);
        _publicationHitScratch.Clear();
        _publicationScrollScratch.Clear();
        _publicationFocusScopeScratch.Clear();
        _publicationRenderScratch.Clear();
        CaptureHitRecords(canvas, transform, layoutPages, _sparseContexts[root]);
        if (_sparseHitStarts[root] < 0)
            return false;
        FixSparseStarts(root, end);
        for (int index = 0; index < _publicationRenderScratch.Count; index++)
        {
            UIRenderRecord record = _publicationRenderScratch[index];
            // A draw that cannot publish needs the full capture diagnostic.
            if (record.Visible && (record.CaptureFailure is not null ||
                (directWorld ? record.FrozenWorldCommand is null :
                    !record.SupportsBatch && record.FrozenCommand is null)))
                return false;
        }

        _sparseRanges.Add(new UIPublicationSparseRange(root, end,
            oldHitStart, oldHitCount, oldScrollStart, oldScrollCount,
            oldScopeStart, oldScopeCount, oldRenderStart, oldRenderCount,
            _sparseNewHits.Count, _publicationHitScratch.Count,
            _sparseNewScroll.Count, _publicationScrollScratch.Count,
            _sparseNewScopes.Count, _publicationFocusScopeScratch.Count,
            _sparseNewRenders.Count, _publicationRenderScratch.Count));
        _sparseNewHits.AddRange(_publicationHitScratch);
        _sparseNewScroll.AddRange(_publicationScrollScratch);
        _sparseNewScopes.AddRange(_publicationFocusScopeScratch);
        _sparseNewRenders.AddRange(_publicationRenderScratch);
        hitDelta += _publicationHitScratch.Count - oldHitCount;
        scrollDelta += _publicationScrollScratch.Count - oldScrollCount;
        scopeDelta += _publicationFocusScopeScratch.Count - oldScopeCount;
        renderDelta += _publicationRenderScratch.Count - oldRenderCount;
        return true;
    }

    /// <summary>Checks that every recaptured record keeps its slot in the previous stores.</summary>
    private bool KeepsSparsePlacement(UIPublication previous)
    {
        ReadOnlySpan<UIFocusScopeRecord> oldScopes = previous.FocusScopes;
        for (int rangeIndex = 0; rangeIndex < _sparseRanges.Count; rangeIndex++)
        {
            UIPublicationSparseRange range = _sparseRanges[rangeIndex];
            if (!range.KeepsCounts)
                return false;
            for (int index = 0; index < range.NewRenderCount; index++)
                if (_sparseNewRenders[range.NewRenderStart + index].MembershipId !=
                    _sparseRenderMemberships[range.OldRenderStart + index])
                    return false;
            for (int index = 0; index < range.NewScopeCount; index++)
                if (_sparseNewScopes[range.NewScopeStart + index].Identity !=
                    oldScopes[range.OldScopeStart + index].Identity)
                    return false;
        }
        return true;
    }

    /// <summary>Records only the changed records; their slots are unchanged.</summary>
    private void BuildSparsePatches(UIPublication previous)
    {
        _sparseHitPatches.Clear();
        _sparseRenderPatches.Clear();
        _sparseScrollReplacement = null;
        _sparseScopeReplacement = null;
        UIRecordStore<UIRenderRecord> renders = previous.RenderRecordStore;
        UIRecordStore<UIHitRecord> hits = previous.HitRecordStore;
        ReadOnlySpan<UIScrollRecord> oldScroll = previous.ScrollReceivers;
        ReadOnlySpan<UIFocusScopeRecord> oldScopes = previous.FocusScopes;
        for (int rangeIndex = 0; rangeIndex < _sparseRanges.Count; rangeIndex++)
        {
            UIPublicationSparseRange range = _sparseRanges[rangeIndex];
            for (int index = 0; index < range.NewRenderCount; index++)
            {
                UIRenderRecord record = _sparseNewRenders[range.NewRenderStart + index];
                if (!previous.TryGetRenderRecordIndex(record.MembershipId, out int renderIndex))
                    throw new InvalidOperationException(
                        "UI.SparseCapture.MembershipMissing: a kept render membership has no previous slot.");
                ref readonly UIRenderRecord old = ref renders[renderIndex];
                if (!old.Equals(record))
                    _sparseRenderPatches.Add((renderIndex, old, record));
            }
            for (int index = 0; index < range.NewHitCount; index++)
            {
                UIHitRecord record = _sparseNewHits[range.NewHitStart + index];
                if (!hits[range.OldHitStart + index].Equals(record))
                    _sparseHitPatches.Add((range.OldHitStart + index, record));
            }
            for (int index = 0; index < range.NewScrollCount; index++)
            {
                UIScrollRecord record = _sparseNewScroll[range.NewScrollStart + index];
                if (oldScroll[range.OldScrollStart + index].Equals(record))
                    continue;
                _sparseScrollReplacement ??= oldScroll.ToArray();
                _sparseScrollReplacement[range.OldScrollStart + index] = record;
            }
            for (int index = 0; index < range.NewScopeCount; index++)
            {
                UIFocusScopeRecord old = oldScopes[range.OldScopeStart + index];
                // Render membership and order are unchanged, so painter order is unchanged.
                UIFocusScopeRecord record = _sparseNewScopes[range.NewScopeStart + index] with
                {
                    PainterOrder = old.PainterOrder,
                };
                if (old.Equals(record))
                    continue;
                _sparseScopeReplacement ??= oldScopes.ToArray();
                _sparseScopeReplacement[range.OldScopeStart + index] = record;
            }
        }
    }

    /// <summary>
    /// Describes a count-changing capture as edits of the previous stores. Hit, scroll, and
    /// focus-scope records follow tree order, so each recaptured range is one edit. The
    /// recaptured walk state then moves to the new record coordinates. No clean node or
    /// clean record is visited.
    /// </summary>
    private void SpliceSparseRecords(UIPublication previous, int nodeCount,
        int hitDelta, int scrollDelta, int scopeDelta, int renderDelta)
    {
        _sparseHitSplices.Clear();
        _sparseScrollReplacement = null;
        _sparseScopeReplacement = null;
        ReadOnlySpan<UIScrollRecord> oldScroll = previous.ScrollReceivers;
        ReadOnlySpan<UIFocusScopeRecord> oldScopes = previous.FocusScopes;
        bool scrollChanged = false;
        bool scopesChanged = false;
        for (int rangeIndex = 0; rangeIndex < _sparseRanges.Count; rangeIndex++)
        {
            UIPublicationSparseRange range = _sparseRanges[rangeIndex];
            if (range.OldHitCount != 0 || range.NewHitCount != 0)
                _sparseHitSplices.Add(new UIRecordSplice(range.OldHitStart, range.OldHitCount,
                    range.NewHitStart, range.NewHitCount));
            scrollChanged |= range.OldScrollCount != 0 || range.NewScrollCount != 0;
            scopesChanged |= range.OldScopeCount != 0 || range.NewScopeCount != 0;
        }
        // Scroll receivers and open focus scopes are few, so they stay flat arrays.
        if (scrollChanged)
            _sparseScrollReplacement = SpliceFlat(oldScroll, scrollDelta,
                static range => (range.OldScrollStart, range.OldScrollCount,
                    range.NewScrollStart, range.NewScrollCount),
                CollectionsMarshal.AsSpan(_sparseNewScroll));
        if (scopesChanged || !oldScopes.IsEmpty)
            _sparseScopeReplacement = SpliceFlat(oldScopes, scopeDelta,
                static range => (range.OldScopeStart, range.OldScopeCount,
                    range.NewScopeStart, range.NewScopeCount),
                CollectionsMarshal.AsSpan(_sparseNewScopes));

        // The walk-order membership list moves with the recaptured ranges.
        int oldRenderTotal = _sparseRenderStarts[nodeCount];
        if (_sparseSpareMemberships.Length < oldRenderTotal + renderDelta)
            _sparseSpareMemberships = new long[oldRenderTotal + renderDelta];
        long[] memberships = _sparseSpareMemberships;
        int renderCursor = 0;
        int membershipWrite = 0;
        for (int rangeIndex = 0; rangeIndex < _sparseRanges.Count; rangeIndex++)
        {
            UIPublicationSparseRange range = _sparseRanges[rangeIndex];
            int clean = range.OldRenderStart - renderCursor;
            Array.Copy(_sparseRenderMemberships, renderCursor, memberships, membershipWrite, clean);
            membershipWrite += clean;
            for (int index = 0; index < range.NewRenderCount; index++)
                memberships[membershipWrite++] =
                    _sparseNewRenders[range.NewRenderStart + index].MembershipId;
            renderCursor = range.OldRenderStart + range.OldRenderCount;
        }
        Array.Copy(_sparseRenderMemberships, renderCursor, memberships, membershipWrite,
            oldRenderTotal - renderCursor);
        _sparseSpareMemberships = _sparseRenderMemberships;
        _sparseRenderMemberships = memberships;
        ShiftSparseStarts(nodeCount, hitDelta, scrollDelta, scopeDelta, renderDelta);
    }

    private T[] SpliceFlat<T>(ReadOnlySpan<T> old, int delta,
        Func<UIPublicationSparseRange, (int OldStart, int OldCount, int NewStart, int NewCount)> select,
        ReadOnlySpan<T> inserted)
    {
        T[] result = new T[old.Length + delta];
        int cursor = 0;
        int write = 0;
        for (int rangeIndex = 0; rangeIndex < _sparseRanges.Count; rangeIndex++)
        {
            (int oldStart, int oldCount, int newStart, int newCount) = select(_sparseRanges[rangeIndex]);
            old[cursor..oldStart].CopyTo(result.AsSpan(write));
            write += oldStart - cursor;
            inserted.Slice(newStart, newCount).CopyTo(result.AsSpan(write));
            write += newCount;
            cursor = oldStart + oldCount;
        }
        old[cursor..].CopyTo(result.AsSpan(write));
        return result;
    }

    /// <summary>
    /// Describes the render changes as ordered edits of the previous render store. Renders
    /// follow painter order, which did not change, so each removed record is found and each
    /// recaptured record is placed by a binary search of its painter position. Returns false
    /// when a record does not follow the painter order; the caller then captures in full.
    /// </summary>
    private bool TryBuildRenderSplices(UIPublication previous, UIRenderPainterIndex painterIndex)
    {
        _sparseRenderSplices.Clear();
        _sparseSplicedRenders.Clear();
        _sparseRemovedRenders.Clear();
        _sparseInsertedRenders.Clear();
        _sparseBackdropDelta = 0;
        _sparsePreRenderDelta = 0;
        UIRecordStore<UIRenderRecord> renders = previous.RenderRecordStore;
        for (int rangeIndex = 0; rangeIndex < _sparseRanges.Count; rangeIndex++)
        {
            UIPublicationSparseRange range = _sparseRanges[rangeIndex];
            for (int index = 0; index < range.OldRenderCount; index++)
            {
                long membership = _sparseRenderMemberships[range.OldRenderStart + index];
                // A membership outside the painter order was never published.
                if (!painterIndex.TryGetPosition(membership, out _))
                    continue;
                if (!painterIndex.TryFind(renders, membership, out int renderIndex))
                    return false;
                _sparseRemovedRenders.Add(renderIndex);
                ref readonly UIRenderRecord old = ref renders[renderIndex];
                _sparseBackdropDelta -= BackdropCount(old);
                _sparsePreRenderDelta -= PreRenderCount(old);
            }
            for (int index = 0; index < range.NewRenderCount; index++)
            {
                UIRenderRecord record = _sparseNewRenders[range.NewRenderStart + index];
                // A full capture also leaves out records that the painter order does not name.
                if (!painterIndex.TryGetPosition(record.MembershipId, out int position))
                    continue;
                _sparseInsertedRenders.Add((position, record));
                _sparseBackdropDelta += BackdropCount(record);
                _sparsePreRenderDelta += PreRenderCount(record);
            }
        }
        _sparseRemovedRenders.Sort();
        _sparseInsertedRenders.Sort(PainterPositionOrder);
        for (int index = 1; index < _sparseRemovedRenders.Count; index++)
            if (_sparseRemovedRenders[index] == _sparseRemovedRenders[index - 1])
                return false;
        for (int index = 1; index < _sparseInsertedRenders.Count; index++)
            if (_sparseInsertedRenders[index].Position == _sparseInsertedRenders[index - 1].Position)
                return false;

        // Merge removals and insertions in old-store order. An insertion at an index goes
        // before the old record at that index, which can itself be removed.
        int removed = 0;
        int insertedIndex = 0;
        int insertAt = -1;
        UIRecordSplice current = default;
        bool open = false;
        while (removed < _sparseRemovedRenders.Count || insertedIndex < _sparseInsertedRenders.Count)
        {
            if (insertedIndex < _sparseInsertedRenders.Count && insertAt < 0)
            {
                insertAt = painterIndex.LowerBound(renders, _sparseInsertedRenders[insertedIndex].Position);
                if (insertAt < 0)
                    return false;
            }
            int removeAt = removed < _sparseRemovedRenders.Count
                ? _sparseRemovedRenders[removed] : int.MaxValue;
            bool insertion = insertedIndex < _sparseInsertedRenders.Count && insertAt <= removeAt;
            int at = insertion ? insertAt : removeAt;
            if (!open || at != current.OldStart + current.OldCount)
            {
                if (open)
                    _sparseRenderSplices.Add(current);
                current = new UIRecordSplice(at, 0, _sparseSplicedRenders.Count, 0);
                open = true;
            }
            if (insertion)
            {
                _sparseSplicedRenders.Add(_sparseInsertedRenders[insertedIndex++].Record);
                current = current with { NewCount = current.NewCount + 1 };
                insertAt = -1;
            }
            else
            {
                current = current with { OldCount = current.OldCount + 1 };
                removed++;
            }
        }
        if (open)
            _sparseRenderSplices.Add(current);
        return true;
    }

    /// <summary>Moves starts outside recaptured subtrees to the spliced record coordinates.</summary>
    private void ShiftSparseStarts(int nodeCount, int hitDelta, int scrollDelta, int scopeDelta,
        int renderDelta)
    {
        int hitShift = 0;
        int scrollShift = 0;
        int scopeShift = 0;
        int renderShift = 0;
        int cleanStart = 0;
        for (int rangeIndex = 0; rangeIndex <= _sparseRanges.Count; rangeIndex++)
        {
            bool last = rangeIndex == _sparseRanges.Count;
            int cleanEnd = last ? nodeCount + 1 : _sparseRanges[rangeIndex].Root;
            if (hitShift != 0 || scrollShift != 0 || scopeShift != 0 || renderShift != 0)
                for (int index = cleanStart; index < cleanEnd; index++)
                {
                    _sparseHitStarts[index] += hitShift;
                    _sparseScrollStarts[index] += scrollShift;
                    _sparseScopeStarts[index] += scopeShift;
                    _sparseRenderStarts[index] += renderShift;
                }
            if (last)
                break;
            UIPublicationSparseRange range = _sparseRanges[rangeIndex];
            hitShift += range.NewHitCount - range.OldHitCount;
            scrollShift += range.NewScrollCount - range.OldScrollCount;
            scopeShift += range.NewScopeCount - range.OldScopeCount;
            renderShift += range.NewRenderCount - range.OldRenderCount;
            cleanStart = range.End;
        }
        if (hitShift != hitDelta || scrollShift != scrollDelta ||
            scopeShift != scopeDelta || renderShift != renderDelta)
            throw new InvalidOperationException(
                "UI.SparseCapture.SpliceMismatch: spliced record counts do not match the recaptured subtrees.");
    }

    /// <summary>Builds the patched or spliced publication. The publication owns both new store claims.</summary>
    private UIPublication CreateSparsePublication(UIPublication previous, ulong generation,
        ulong layoutGeneration, ulong hitGeneration, long changeRevision,
        UILayoutOutputPageView layoutPages, UIScrollRecord[] scroll, UIFocusScopeRecord[] scopes,
        UIRenderSurface? surfaceWriter, UIDrawPayload? quadPayload, Matrix4x4 quadWorldMatrix,
        UIRenderPainterIndex painterIndex)
    {
        if (_sparseSpliced)
            return CreateSplicedPublication(previous, generation, layoutGeneration, hitGeneration,
                changeRevision, layoutPages, scroll, scopes, surfaceWriter, quadPayload,
                quadWorldMatrix, painterIndex);

        _sparseRenderPatches.Sort(RenderPatchOrder);
        int backdropDelta = 0;
        int preRenderDelta = 0;
        for (int index = 0; index < _sparseRenderPatches.Count; index++)
        {
            (_, UIRenderRecord old, UIRenderRecord record) = _sparseRenderPatches[index];
            backdropDelta += BackdropCount(record) - BackdropCount(old);
            preRenderDelta += PreRenderCount(record) - PreRenderCount(old);
        }

        UIRecordStore<UIHitRecord> hitStore = PatchRecordStore(previous.HitRecordStore,
            CollectionsMarshal.AsSpan(_sparseHitPatches), static patch => patch.Index,
            static patch => patch.Record, out _);
        UIRecordStore<UIRenderRecord> renderStore;
        int[] changedRenderPages;
        try
        {
            renderStore = PatchRecordStore(previous.RenderRecordStore,
                CollectionsMarshal.AsSpan(_sparseRenderPatches), static patch => patch.Index,
                static patch => patch.New, out changedRenderPages);
        }
        catch
        {
            hitStore.Release();
            throw;
        }
        return UIPublication.CreatePatched(previous, generation, layoutGeneration,
            hitGeneration, changeRevision, layoutPages, hitStore, renderStore,
            changedRenderPages, backdropDelta, preRenderDelta, scroll, scopes,
            surfaceWriter, quadPayload, quadWorldMatrix);
    }

    private UIPublication CreateSplicedPublication(UIPublication previous, ulong generation,
        ulong layoutGeneration, ulong hitGeneration, long changeRevision,
        UILayoutOutputPageView layoutPages, UIScrollRecord[] scroll, UIFocusScopeRecord[] scopes,
        UIRenderSurface? surfaceWriter, UIDrawPayload? quadPayload, Matrix4x4 quadWorldMatrix,
        UIRenderPainterIndex painterIndex)
    {
        UIRecordStore<UIHitRecord> hitStore = previous.HitRecordStore.Splice(
            CollectionsMarshal.AsSpan(_sparseHitSplices), CollectionsMarshal.AsSpan(_sparseNewHits));
        UIRecordStore<UIRenderRecord> renderStore;
        try
        {
            renderStore = previous.RenderRecordStore.Splice(
                CollectionsMarshal.AsSpan(_sparseRenderSplices),
                CollectionsMarshal.AsSpan(_sparseSplicedRenders));
        }
        catch
        {
            hitStore.Release();
            throw;
        }
        try
        {
            // Render indices moved, so each open scope finds its last painted record again.
            if (scopes.Length > 0 && ReferenceEquals(scopes, _sparseScopeReplacement))
                for (int index = 0; index < scopes.Length; index++)
                    scopes[index] = scopes[index] with
                    {
                        PainterOrder = LastPaintedRecord(scopes[index], renderStore, painterIndex),
                    };
        }
        catch
        {
            renderStore.Release();
            hitStore.Release();
            throw;
        }
        return UIPublication.CreateSpliced(previous, generation, layoutGeneration,
            hitGeneration, changeRevision, layoutPages, hitStore, renderStore,
            CollectionsMarshal.AsSpan(_sparseRenderSplices),
            CollectionsMarshal.AsSpan(_sparseSplicedRenders), _sparseBackdropDelta,
            _sparsePreRenderDelta, scroll, scopes, surfaceWriter, quadPayload, quadWorldMatrix);
    }

    /// <summary>
    /// Gets the store index of the last painted record that a scope subtree captured, or -1.
    /// The walk-order membership range of the subtree names its records.
    /// </summary>
    private int LastPaintedRecord(UIFocusScopeRecord scope, UIRecordStore<UIRenderRecord> renders,
        UIRenderPainterIndex painterIndex)
    {
        int lastPosition = -1;
        long lastMembership = 0;
        for (int index = _sparseRenderStarts[scope.NodeIndex];
            index < _sparseRenderStarts[scope.SubtreeEndIndex]; index++)
            if (painterIndex.TryGetPosition(_sparseRenderMemberships[index], out int position) &&
                position > lastPosition)
            {
                lastPosition = position;
                lastMembership = _sparseRenderMemberships[index];
            }
        return lastPosition >= 0 && painterIndex.TryFind(renders, lastMembership, out int found)
            ? found : -1;
    }

    private static int BackdropCount(in UIRenderRecord record)
        => record.Visible && record.Backdrop.IsDefined ? 1 : 0;

    private static int PreRenderCount(in UIRenderRecord record)
        => record.Visible && record.RenderPass == (int)Data.Rendering.EDefaultRenderPass.PreRender ? 1 : 0;

    /// <summary>Copies each changed page once and shares all other record pages.</summary>
    private static UIRecordStore<T> PatchRecordStore<T, TPatch>(UIRecordStore<T> store,
        ReadOnlySpan<TPatch> patches, Func<TPatch, int> indexOf, Func<TPatch, T> recordOf,
        out int[] changedPages) where T : struct
    {
        int pageCount = 0;
        int lastPage = -1;
        for (int index = 0; index < patches.Length; index++)
        {
            int page = store.LocatePage(indexOf(patches[index]), out _);
            if (page != lastPage)
            {
                pageCount++;
                lastPage = page;
            }
        }
        changedPages = pageCount == 0 ? [] : new int[pageCount];
        T[][] pages = pageCount == 0 ? [] : new T[pageCount][];
        lastPage = -1;
        int pageSlot = -1;
        for (int index = 0; index < patches.Length; index++)
        {
            int page = store.LocatePage(indexOf(patches[index]), out int offset);
            if (page != lastPage)
            {
                pageSlot++;
                lastPage = page;
                changedPages[pageSlot] = page;
                pages[pageSlot] = store.GetPage(page).ToArray();
            }
            pages[pageSlot][offset] = recordOf(patches[index]);
        }
        return store.WithReplacedPages(changedPages, pages);
    }

    private void AcceptSparseBasis(UIPublication replacement, UICommittedChangeCursor cursor)
    {
        _sparseBasis = _sparseOrderValid ? replacement : null;
        _sparseCursor = cursor;
    }

    private void RecordSparseNode(int nodeIndex, UITransform transform,
        UIPublicationCaptureContext context)
    {
        if ((uint)nodeIndex >= (uint)_sparseTransforms.Length)
        {
            // A caller that captures outside a prepared walk cannot describe a sparse basis.
            _sparseOrderValid = false;
            return;
        }
        _sparseTransforms[nodeIndex] = transform;
        _sparseContexts[nodeIndex] = context;
        _sparseHiddenRoots[nodeIndex] = -1;
        _sparseHitStarts[nodeIndex] = _sparseHitBase + _publicationHitScratch.Count;
        _sparseScrollStarts[nodeIndex] = _sparseScrollBase + _publicationScrollScratch.Count;
        _sparseScopeStarts[nodeIndex] = _sparseScopeBase + _publicationFocusScopeScratch.Count;
        _sparseRenderStarts[nodeIndex] = _sparseRenderBase + _publicationRenderScratch.Count;
    }

    private void RecordPointerDependencies(UIComponent component, int dependent)
    {
        switch (component)
        {
            case UISplitterHandleComponent { SplitTarget: { } splitTarget }:
                _sparseDependencies.Add((splitTarget.ElementIdentity, dependent));
                break;
            case UIScrollbarComponent scrollbar:
                if (scrollbar.ScrollTarget is { } scrollTarget)
                    _sparseDependencies.Add((scrollTarget.ElementIdentity, dependent));
                if (scrollbar.ThumbTransform is { } thumb)
                    _sparseDependencies.Add((thumb.ElementIdentity, dependent));
                break;
        }
    }

    private void RemoveSparseDependencies(int start, int end)
    {
        int kept = 0;
        for (int index = 0; index < _sparseDependencies.Count; index++)
        {
            (UIElementIdentity, int Dependent) entry = _sparseDependencies[index];
            if (entry.Dependent >= start && entry.Dependent < end)
                continue;
            _sparseDependencies[kept++] = entry;
        }
        _sparseDependencies.RemoveRange(kept, _sparseDependencies.Count - kept);
    }

    private void EnsureSparseCapacity(int nodeCount)
    {
        if (_sparseTransforms.Length != nodeCount)
        {
            _sparseTransforms = new UITransform?[nodeCount];
            _sparseContexts = new UIPublicationCaptureContext[nodeCount];
            _sparseSubtreeEnds = new int[nodeCount];
            _sparseHiddenRoots = new int[nodeCount];
            _sparseMarks = new int[nodeCount];
            _sparseMarkStamp = 0;
            _sparseHitStarts = new int[nodeCount + 1];
            _sparseScrollStarts = new int[nodeCount + 1];
            _sparseScopeStarts = new int[nodeCount + 1];
            _sparseRenderStarts = new int[nodeCount + 1];
        }
        else
            Array.Clear(_sparseTransforms);
    }

    private void SetSparseWalkBases(int hits, int scroll, int scopes, int renders)
    {
        _sparseHitBase = hits;
        _sparseScrollBase = scroll;
        _sparseScopeBase = scopes;
        _sparseRenderBase = renders;
    }

    private void ResetSparseStarts(int start, int end)
    {
        _sparseHitStarts.AsSpan(start, end - start).Fill(-1);
        _sparseScrollStarts.AsSpan(start, end - start).Fill(-1);
        _sparseScopeStarts.AsSpan(start, end - start).Fill(-1);
        _sparseRenderStarts.AsSpan(start, end - start).Fill(-1);
    }

    /// <summary>
    /// Gives nodes inside a hidden subtree the start of the next record, and names the
    /// visited hidden node that pruned them. In preorder, every node between a pruned
    /// node and an unvisited descendant is also unvisited.
    /// </summary>
    private void FixSparseStarts(int start, int end)
    {
        for (int index = start + 1; index < end; index++)
        {
            if (_sparseHitStarts[index] >= 0)
                continue;
            _sparseHitStarts[index] = _sparseHitStarts[index - 1];
            _sparseScrollStarts[index] = _sparseScrollStarts[index - 1];
            _sparseScopeStarts[index] = _sparseScopeStarts[index - 1];
            _sparseRenderStarts[index] = _sparseRenderStarts[index - 1];
            _sparseHiddenRoots[index] = _sparseHiddenRoots[index - 1] >= 0
                ? _sparseHiddenRoots[index - 1] : index - 1;
        }
    }

    private int NextSparseStamp()
    {
        if (++_sparseMarkStamp == int.MaxValue)
        {
            Array.Clear(_sparseMarks);
            _sparseMarkStamp = 1;
        }
        return _sparseMarkStamp;
    }

    /// <summary>Adds a changed node unless it is already marked or lies in a hidden subtree.</summary>
    private bool AddSparseRoot(int nodeIndex, int stamp)
    {
        // A node in a hidden subtree has no records while its hidden ancestor is unchanged.
        if (_sparseHiddenRoots[nodeIndex] >= 0 || _sparseMarks[nodeIndex] == stamp)
            return false;
        _sparseMarks[nodeIndex] = stamp;
        _sparseRoots.Add(nodeIndex);
        return true;
    }

    /// <summary>Sorts changed nodes and keeps only the roots of disjoint subtrees.</summary>
    private void NormalizeSparseRoots()
    {
        _sparseRoots.Sort();
        int kept = 0;
        int coveredEnd = -1;
        for (int index = 0; index < _sparseRoots.Count; index++)
        {
            int root = _sparseRoots[index];
            if (root < coveredEnd)
                continue;
            _sparseRoots[kept++] = root;
            coveredEnd = _sparseSubtreeEnds[root];
        }
        _sparseRoots.RemoveRange(kept, _sparseRoots.Count - kept);
    }

    private bool IsCoveredBySparseRoot(int nodeIndex)
    {
        int low = 0;
        int high = _sparseRoots.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            if (_sparseRoots[middle] <= nodeIndex)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return high >= 0 && nodeIndex < _sparseSubtreeEnds[_sparseRoots[high]];
    }

    private static bool SameRecords<T>(ReadOnlySpan<T> records, UIRecordStore<T> store)
        where T : struct, IEquatable<T>
    {
        if (records.Length != store.Length)
            return false;
        for (int page = 0; page < store.PageCount; page++)
        {
            ReadOnlySpan<T> values = store.GetPage(page);
            if (!records.Slice(store.GetPageStart(page), values.Length).SequenceEqual(values))
                return false;
        }
        return true;
    }

    private static bool SameLayoutOutput(in UILayoutNodeOutput left, in UILayoutNodeOutput right)
        => left.RawSize == right.RawSize &&
            left.DesiredSize == right.DesiredSize &&
            left.MeasureConstraint == right.MeasureConstraint &&
            left.ActualSize == right.ActualSize &&
            left.LocalTranslation == right.LocalTranslation &&
            left.PlacementTranslation == right.PlacementTranslation &&
            left.AcceptedScrollOffset == right.AcceptedScrollOffset &&
            left.AcceptedMaxScrollOffset == right.AcceptedMaxScrollOffset &&
            left.AcceptedSplitExtent == right.AcceptedSplitExtent &&
            left.AcceptedSplitFirstSize == right.AcceptedSplitFirstSize &&
            left.AcceptedSplitSecondSize == right.AcceptedSplitSecondSize &&
            left.AcceptedSplitFixedSide == right.AcceptedSplitFixedSide &&
            left.AcceptedSplitVertical == right.AcceptedSplitVertical &&
            left.AllocatedBounds == right.AllocatedBounds &&
            left.Visible == right.Visible &&
            ReferenceEquals(left.TextResult, right.TextResult);
}
