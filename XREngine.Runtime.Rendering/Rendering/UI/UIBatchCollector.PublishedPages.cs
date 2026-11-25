using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>
/// Collects published UI records one immutable record page at a time.
/// <para>
/// Runs never cross a page boundary. A page that a later publication shares keeps
/// its runs, its unbatched draw positions, and its primitive handles, so collection
/// visits only the records of new pages. The cost of this is at most one extra run
/// for each render pass at each page boundary.
/// </para>
/// </summary>
public sealed partial class UIBatchCollector
{
    /// <summary>One run start or one unbatched record in a collected record page.</summary>
    private readonly record struct PublishedPageEvent(int Offset, int Pass, int ZIndex,
        MaterialQuadBatchData? Material, TextBatchData? Text);

    /// <summary>A composition segment span that a collected page uses, relative to the page start.</summary>
    private readonly record struct PublishedPageSegment(int Start, int End, ulong Identity, ulong Revision);

    /// <summary>The open run of one render pass while a page is collected.</summary>
    private readonly record struct PageRunCursor(EBatchMarkerKind Kind, int ZIndex, object Run);

    /// <summary>
    /// The collected output of one immutable record page. Its runs do not change after
    /// collection, so the collect side and the render side can share them.
    /// </summary>
    private sealed class PublishedPage
    {
        public UIRecordStoreNode<UIRenderRecord>? Leaf;
        public readonly List<PublishedPageEvent> Events = [];
        public readonly List<PublishedPageSegment> Segments = [];
        public readonly List<ulong> Handles = [];
        public int UnbatchedDrawCount;
        public bool PendingTextLayout;

        public void Reset()
        {
            Leaf = null;
            Events.Clear();
            Segments.Clear();
            Handles.Clear();
            UnbatchedDrawCount = 0;
            PendingTextLayout = false;
        }
    }

    // The pages of the most recent completed collection, committed or not.
    private Dictionary<UIRecordStoreNode<UIRenderRecord>, PublishedPage> _latestPages =
        new(ReferenceEqualityComparer.Instance);
    private Dictionary<UIRecordStoreNode<UIRenderRecord>, PublishedPage> _nextPages =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<PublishedPage> _newPages = [];
    private readonly List<PublishedPage> _droppedPages = [];
    private readonly Stack<PublishedPage> _freePages = [];
    private readonly Dictionary<int, PageRunCursor> _pageRunCursors = [];
    // Runs of dropped pages. The render side can read them until the next swap.
    private readonly List<MaterialQuadBatchData> _retiredMaterialRuns = [];
    private readonly List<TextBatchData> _retiredTextRuns = [];
    private readonly Stack<MaterialQuadBatchData> _freeMaterialRuns = [];
    private readonly Stack<TextBatchData> _freeTextRuns = [];
    private bool _pageBuildActive;
    private int _collectPublishedRunMarkers;
    private int _renderPublishedRunMarkers;
    private int _collectUnbatchedDrawCount;
    private int _renderUnbatchedDrawCount;
    private bool _collectPendingTextLayout;
    private bool _renderPendingTextLayout;

    /// <summary>The number of record pages that the last published collection visited.</summary>
    internal int LastCollectedPageCount { get; private set; }

    /// <summary>The number of record pages that the last published collection reused without a visit.</summary>
    internal int LastReusedPageCount { get; private set; }

    /// <summary>The number of records that the last published collection visited.</summary>
    internal int LastCollectedPageRecordCount { get; private set; }

    private void CollectPublishedPages(UIRecordStore<UIRenderRecord> records,
        UICompositionSegmentSet composition, RenderCommandCollection passes,
        Action<UIRenderRecord> collectUnbatched, long collectionEpoch)
    {
        AbandonPendingPages();
        ResetCollectedPageSummary();
        LastCollectedPageCount = 0;
        LastReusedPageCount = 0;
        LastCollectedPageRecordCount = 0;
        _pageBuildActive = true;
        try
        {
            for (int page = 0; page < records.PageCount; page++)
            {
                UIRecordStoreNode<UIRenderRecord> leaf = records.GetLeaf(page);
                int start = records.GetPageStart(page);
                ReadOnlySpan<UIRenderRecord> pageRecords = leaf.Values!;
                if (_latestPages.TryGetValue(leaf, out PublishedPage? collected) &&
                    SegmentsMatch(collected, composition, start, pageRecords.Length))
                    LastReusedPageCount++;
                else
                    collected = CollectPage(leaf, pageRecords, start, composition);
                if (!_nextPages.TryAdd(leaf, collected))
                    throw new InvalidOperationException(
                        "UI.Batch.DuplicateRecordPage: a published record store holds one record page twice.");
                if (!EmitPage(collected, page, pageRecords, passes, collectUnbatched, collectionEpoch))
                    return;
            }
            if (Volatile.Read(ref _publishedCollectionEpoch) != collectionEpoch)
                return;
            FinishPageCollection();
        }
        catch
        {
            if (Volatile.Read(ref _publishedCollectionEpoch) == collectionEpoch)
                AbandonPendingPages();
            throw;
        }
    }

    /// <summary>Replays the committed painter stream for a publication that did not change.</summary>
    private void EmitCommittedPublishedStream(UIRecordStore<UIRenderRecord> records,
        RenderCommandCollection passes, Action<UIRenderRecord> collectUnbatched, long collectionEpoch)
    {
        for (int index = 0; index < _renderPublishedMarkers.Count; index++)
        {
            PublishedMarker marker = _renderPublishedMarkers[index];
            if (marker.GroupIndex >= 0)
            {
                passes.AddCPU(AcquireMarker(marker.Pass, marker.ZIndex, marker.Kind, marker.GroupIndex));
                continue;
            }
            collectUnbatched(records.GetPage(marker.Page)[marker.Offset]);
            if (Volatile.Read(ref _publishedCollectionEpoch) != collectionEpoch)
                return;
        }
    }

    private PublishedPage CollectPage(UIRecordStoreNode<UIRenderRecord> leaf,
        ReadOnlySpan<UIRenderRecord> records, int start, UICompositionSegmentSet composition)
    {
        PublishedPage page = _freePages.Count != 0 ? _freePages.Pop() : new PublishedPage();
        page.Leaf = leaf;
        // Register first, so that a failure below releases the handles taken so far.
        _newPages.Add(page);
        LastCollectedPageCount++;
        LastCollectedPageRecordCount += records.Length;
        WriteSegments(page, composition, start, records.Length);
        lock (_primitiveSlotGate)
            foreach (ref readonly UIRenderRecord record in records)
                page.Handles.Add(RetainPagePrimitiveLocked(record.MembershipId, record.Identity));

        _pageRunCursors.Clear();
        // Records and segments share painter order, so one cursor walks both.
        int segmentCursor = composition.LowerBound(start);
        for (int offset = 0; offset < records.Length; offset++)
        {
            ref readonly UIRenderRecord record = ref records[offset];
            if (!record.Visible)
                continue;
            if (!record.SupportsBatch)
            {
                // An unbatched draw sits between runs, so the next batched record starts a new run.
                _pageRunCursors.Remove(record.RenderPass);
                page.Events.Add(new(offset, record.RenderPass, record.ZIndex, null, null));
                if (record.FrozenCommand is not null)
                    page.UnbatchedDrawCount++;
                continue;
            }
            UICompositionSegment? segment = composition.Advance(ref segmentCursor, start + offset);
            ulong segmentIdentity = segment?.Identity ?? 0;
            ulong segmentRevision = segment?.Revision ?? 0;
            if (record.IsText)
                CollectPageText(page, offset, in record, page.Handles[offset], segmentIdentity, segmentRevision);
            else
                CollectPageMaterial(page, offset, in record, page.Handles[offset], segmentIdentity, segmentRevision);
        }
        _pageRunCursors.Clear();
        return page;
    }

    private void CollectPageMaterial(PublishedPage page, int offset, in UIRenderRecord record,
        ulong primitiveHandle, ulong segmentIdentity, ulong segmentRevision)
    {
        int pass = record.RenderPass;
        MaterialQuadBatchData run;
        if (_pageRunCursors.TryGetValue(pass, out PageRunCursor cursor) &&
            cursor.Kind == EBatchMarkerKind.Material && cursor.ZIndex == record.ZIndex &&
            cursor.Run is MaterialQuadBatchData open &&
            open.CompositionOwner == record.CompositionOwner &&
            open.CompositionSegmentIdentity == segmentIdentity &&
            open.CropRegion == record.Crop &&
            ReferenceEquals(open.Texture, record.Texture) &&
            ReferenceEquals(open.ImageSnapshot, record.TextureSnapshot) &&
            open.ImageSrgb == record.ImageSrgb && open.Border == record.IsBorder &&
            open.Backdrop == record.Backdrop)
            run = open;
        else
        {
            run = _freeMaterialRuns.Count != 0 ? _freeMaterialRuns.Pop() : new MaterialQuadBatchData();
            run.RunId = AllocateRunId();
            run.CompositionOwner = record.CompositionOwner;
            run.CompositionSegmentIdentity = segmentIdentity;
            run.CompositionSegmentRevision = segmentRevision;
            run.CropRegion = record.Crop;
            run.Texture = record.Texture;
            run.ImageSnapshot = record.TextureSnapshot;
            run.ImageSrgb = record.ImageSrgb;
            run.Border = record.IsBorder;
            run.Backdrop = record.Backdrop;
            page.Events.Add(new(offset, pass, record.ZIndex, run, null));
            _pageRunCursors[pass] = new(EBatchMarkerKind.Material, record.ZIndex, run);
        }
        Matrix4x4 world = record.WorldMatrix;
        Vector4 color = record.QuadColor;
        Vector4 bounds = record.Bounds;
        AppendMaterialEntry(run, in world, in color, in bounds, record.UV,
            record.BorderColor, record.BorderWidth, primitiveHandle);
    }

    private void CollectPageText(PublishedPage page, int offset, in UIRenderRecord record,
        ulong primitiveHandle, ulong segmentIdentity, ulong segmentRevision)
    {
        if (record.TextLayout is not { } layout || record.Atlas is not { } atlas)
        {
            page.PendingTextLayout |= record.TextLayout is null;
            // Capture rejects these records; reaching this point loses visible text.
            // Collection runs before a receipt is active, so report it directly.
            UIFailureDiagnostics.Report(
                "UI.TextCapture.AtlasPending: a published text record has no layout or font atlas.");
            return;
        }
        if (layout.Glyphs.Length == 0)
            return;

        int pass = record.RenderPass;
        TextBatchData run;
        if (_pageRunCursors.TryGetValue(pass, out PageRunCursor cursor) &&
            cursor.Kind == EBatchMarkerKind.Text && cursor.ZIndex == record.ZIndex &&
            cursor.Run is TextBatchData open &&
            open.CompositionOwner == record.CompositionOwner &&
            open.CompositionSegmentIdentity == segmentIdentity &&
            open.CropRegion == record.Crop &&
            ReferenceEquals(open.Atlas, atlas) &&
            ReferenceEquals(open.AtlasSnapshot, record.AtlasSnapshot) &&
            open.AtlasType == record.AtlasType &&
            open.DistanceRange.Equals(record.DistanceRange) &&
            open.DistanceRangeMiddle.Equals(record.DistanceRangeMiddle) &&
            open.MsdfFillBias.Equals(record.MsdfFillBias) &&
            open.DebugMode == record.DebugMode)
            run = open;
        else
        {
            run = _freeTextRuns.Count != 0 ? _freeTextRuns.Pop() : new TextBatchData();
            run.RunId = AllocateRunId();
            run.CompositionOwner = record.CompositionOwner;
            run.CompositionSegmentIdentity = segmentIdentity;
            run.CompositionSegmentRevision = segmentRevision;
            run.CropRegion = record.Crop;
            run.Atlas = atlas;
            run.AtlasSnapshot = record.AtlasSnapshot;
            run.AtlasType = record.AtlasType;
            run.DistanceRange = record.DistanceRange;
            run.DistanceRangeMiddle = record.DistanceRangeMiddle;
            run.MsdfFillBias = record.MsdfFillBias;
            run.DebugMode = record.DebugMode;
            page.Events.Add(new(offset, pass, record.ZIndex, null, run));
            _pageRunCursors[pass] = new(EBatchMarkerKind.Text, record.ZIndex, run);
        }
        Matrix4x4 world = record.WorldMatrix;
        Vector4 color = record.TextColor;
        Vector4 outline = record.OutlineColor;
        Vector4 bounds = record.Bounds;
        AppendTextEntry(run, pass, record.ZIndex, -1, atlas, in world, in color, in outline,
            record.OutlineThickness, in bounds, record.AtlasType, record.DistanceRange,
            record.DistanceRangeMiddle, record.MsdfFillBias, record.DebugMode, layout, primitiveHandle);
    }

    /// <summary>Adds the runs and unbatched draws of one page to the collect side in painter order.</summary>
    /// <returns>False when a callback ended this collection.</returns>
    private bool EmitPage(PublishedPage page, int pageIndex, ReadOnlySpan<UIRenderRecord> records,
        RenderCommandCollection passes, Action<UIRenderRecord> collectUnbatched, long collectionEpoch)
    {
        _collectUnbatchedDrawCount += page.UnbatchedDrawCount;
        _collectPendingTextLayout |= page.PendingTextLayout;
        // Index the list: a callback can clear the collector and reset this page.
        for (int index = 0; index < page.Events.Count; index++)
        {
            PublishedPageEvent item = page.Events[index];
            int group;
            EBatchMarkerKind kind;
            if (item.Material is { } material)
            {
                group = GetOrCreatePool(_collectMaterialGroups, item.Pass).Adopt(material);
                kind = EBatchMarkerKind.Material;
            }
            else if (item.Text is { } text)
            {
                group = GetOrCreatePool(_collectTextGroups, item.Pass).Adopt(text);
                kind = EBatchMarkerKind.Text;
            }
            else
            {
                _collectPublishedMarkers.Add(new(pageIndex, item.Offset, item.Pass, item.ZIndex,
                    EBatchMarkerKind.Material, -1));
                collectUnbatched(records[item.Offset]);
                if (Volatile.Read(ref _publishedCollectionEpoch) != collectionEpoch)
                    return false;
                continue;
            }
            passes.AddCPU(AcquireMarker(item.Pass, item.ZIndex, kind, group));
            _collectPublishedMarkers.Add(new(pageIndex, item.Offset, item.Pass, item.ZIndex, kind, group));
            _collectPublishedRunMarkers++;
        }
        return true;
    }

    private static void WriteSegments(PublishedPage page, UICompositionSegmentSet composition,
        int start, int length)
    {
        int end = start + length;
        for (int index = composition.LowerBound(start); index < composition.Count; index++)
        {
            UICompositionSegment segment = composition.GetSegment(index);
            if (segment.Start >= end)
                break;
            page.Segments.Add(PageSegment(segment, start, end));
        }
    }

    /// <summary>
    /// Checks that the segments over a page position match the segments that the page
    /// was collected with. Runs store segment identities and revisions, so a page that
    /// moved into a changed or moved segment must be collected again.
    /// </summary>
    private static bool SegmentsMatch(PublishedPage page, UICompositionSegmentSet composition,
        int start, int length)
    {
        int end = start + length;
        int expected = 0;
        for (int index = composition.LowerBound(start); index < composition.Count; index++)
        {
            UICompositionSegment segment = composition.GetSegment(index);
            if (segment.Start >= end)
                break;
            if (expected == page.Segments.Count || page.Segments[expected++] != PageSegment(segment, start, end))
                return false;
        }
        return expected == page.Segments.Count;
    }

    private static PublishedPageSegment PageSegment(UICompositionSegment segment, int start, int end)
        => new(Math.Max(segment.Start, start) - start,
            Math.Min(segment.Start + segment.Count, end) - start,
            segment.Identity, segment.Revision);

    /// <summary>Makes the collected pages the latest page set and drops the pages it does not hold.</summary>
    private void FinishPageCollection()
    {
        _droppedPages.Clear();
        foreach (var pair in _latestPages)
            if (!_nextPages.TryGetValue(pair.Key, out PublishedPage? kept) || !ReferenceEquals(kept, pair.Value))
                _droppedPages.Add(pair.Value);
        ReconcilePageRunIds();
        foreach (PublishedPage dropped in _droppedPages)
            ReleasePage(dropped);
        _droppedPages.Clear();
        _newPages.Clear();
        (_latestPages, _nextPages) = (_nextPages, _latestPages);
        _nextPages.Clear();
        _pageBuildActive = false;
    }

    // Markers keep their ordinal position in the painter stream. GPU cohorts use this
    // separate identity, so an inserted earlier run cannot move later storage. Only new
    // runs take an identity, and only from runs of pages that this collection dropped.
    private void ReconcilePageRunIds()
    {
        _claimedPreviousRunIds.Clear();
        _oldMaterialRunByHandle.Clear();
        _oldTextRunByHandle.Clear();
        LastRunReconcileVisitCount = 0;
        if (_droppedPages.Count == 0 || _newPages.Count == 0)
            return;
        foreach (PublishedPage page in _droppedPages)
            foreach (PublishedPageEvent item in page.Events)
            {
                if (item.Material is { } material)
                    foreach (MaterialQuadEntry entry in material.Entries)
                    {
                        LastRunReconcileVisitCount++;
                        if (entry.PrimitiveHandle != 0)
                            _oldMaterialRunByHandle.TryAdd(entry.PrimitiveHandle, (item.Pass, material));
                    }
                else if (item.Text is { } text)
                    foreach (TextEntry entry in text.Entries)
                    {
                        LastRunReconcileVisitCount++;
                        if (entry.PrimitiveHandle != 0)
                            _oldTextRunByHandle.TryAdd(entry.PrimitiveHandle, (item.Pass, text));
                    }
            }
        foreach (PublishedPage page in _newPages)
            foreach (PublishedPageEvent item in page.Events)
            {
                if (item.Material is { } current)
                    ReconcileMaterialRun(item.Pass, current);
                else if (item.Text is { } text)
                    ReconcileTextRun(item.Pass, text);
            }
        _oldMaterialRunByHandle.Clear();
        _oldTextRunByHandle.Clear();
        _claimedPreviousRunIds.Clear();
    }

    private void ReconcileMaterialRun(int pass, MaterialQuadBatchData current)
    {
        foreach (MaterialQuadEntry entry in current.Entries)
        {
            LastRunReconcileVisitCount++;
            if (entry.PrimitiveHandle == 0 ||
                !_oldMaterialRunByHandle.TryGetValue(entry.PrimitiveHandle, out var match) ||
                match.Pass != pass) continue;
            MaterialQuadBatchData old = match.Run;
            if (_claimedPreviousRunIds.Contains(old.RunId) || current.CropRegion != old.CropRegion ||
                current.CompositionOwner != old.CompositionOwner ||
                current.CompositionSegmentIdentity != old.CompositionSegmentIdentity ||
                !ReferenceEquals(current.Texture, old.Texture) ||
                !ReferenceEquals(current.ImageSnapshot, old.ImageSnapshot) ||
                current.ImageSrgb != old.ImageSrgb || current.Border != old.Border) continue;
            current.RunId = old.RunId;
            _claimedPreviousRunIds.Add(old.RunId);
            return;
        }
    }

    private void ReconcileTextRun(int pass, TextBatchData current)
    {
        foreach (TextEntry entry in current.Entries)
        {
            LastRunReconcileVisitCount++;
            if (entry.PrimitiveHandle == 0 ||
                !_oldTextRunByHandle.TryGetValue(entry.PrimitiveHandle, out var match) ||
                match.Pass != pass) continue;
            TextBatchData old = match.Run;
            if (_claimedPreviousRunIds.Contains(old.RunId) || current.CropRegion != old.CropRegion ||
                current.CompositionOwner != old.CompositionOwner ||
                current.CompositionSegmentIdentity != old.CompositionSegmentIdentity ||
                !ReferenceEquals(current.Atlas, old.Atlas) || current.AtlasType != old.AtlasType ||
                !ReferenceEquals(current.AtlasSnapshot, old.AtlasSnapshot) ||
                current.DistanceRange != old.DistanceRange ||
                current.DistanceRangeMiddle != old.DistanceRangeMiddle ||
                current.MsdfFillBias != old.MsdfFillBias || current.DebugMode != old.DebugMode) continue;
            current.RunId = old.RunId;
            _claimedPreviousRunIds.Add(old.RunId);
            return;
        }
    }

    /// <summary>Releases the primitive handles of a page and retires its runs until the next swap.</summary>
    private void ReleasePage(PublishedPage page)
    {
        lock (_primitiveSlotGate)
            foreach (ulong handle in page.Handles)
                ReleasePagePrimitiveLocked(handle);
        foreach (PublishedPageEvent item in page.Events)
        {
            if (item.Material is { } material)
                _retiredMaterialRuns.Add(material);
            else if (item.Text is { } text)
                _retiredTextRuns.Add(text);
        }
        page.Reset();
        _freePages.Push(page);
    }

    /// <summary>Releases the new pages of a collection that did not complete.</summary>
    private void AbandonPendingPages()
    {
        if (!_pageBuildActive)
            return;
        _pageBuildActive = false;
        foreach (PublishedPage page in _newPages)
            ReleasePage(page);
        _newPages.Clear();
        _nextPages.Clear();
        _pageRunCursors.Clear();
    }

    /// <summary>Returns retired runs to the free pools. Call only after the render side stops reading them.</summary>
    private void RecycleRetiredPageRuns()
    {
        foreach (MaterialQuadBatchData run in _retiredMaterialRuns)
        {
            run.Clear();
            run.RunId = 0;
            _freeMaterialRuns.Push(run);
        }
        _retiredMaterialRuns.Clear();
        foreach (TextBatchData run in _retiredTextRuns)
        {
            run.Clear();
            run.RunId = 0;
            _freeTextRuns.Push(run);
        }
        _retiredTextRuns.Clear();
    }

    private void ResetCollectedPageSummary()
    {
        _collectPublishedRunMarkers = 0;
        _collectUnbatchedDrawCount = 0;
        _collectPendingTextLayout = false;
    }

    private void CommitCollectedPageSummary()
    {
        _renderPublishedRunMarkers = _collectPublishedRunMarkers;
        _renderUnbatchedDrawCount = _collectUnbatchedDrawCount;
        _renderPendingTextLayout = _collectPendingTextLayout;
        ResetCollectedPageSummary();
    }

    /// <summary>Drops every collected page. Call only when the render side also drops its runs.</summary>
    private void ClearPublishedPages()
    {
        AbandonPendingPages();
        foreach (PublishedPage page in _latestPages.Values)
            ReleasePage(page);
        _latestPages.Clear();
        RecycleRetiredPageRuns();
        ResetCollectedPageSummary();
        _renderPublishedRunMarkers = 0;
        _renderUnbatchedDrawCount = 0;
        _renderPendingTextLayout = false;
    }
}
