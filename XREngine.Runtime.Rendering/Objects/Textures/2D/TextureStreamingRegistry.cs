using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Tracks imported streaming textures and produces immutable frame snapshots for policy.
/// Thread-safety: registration and snapshot collection are free-threaded; per-record mutation
/// is protected by <see cref="ImportedTextureStreamingRecord.Sync"/>.
/// </summary>
internal sealed class TextureStreamingRegistry
{
    private readonly ConditionalWeakTable<XRTexture2D, ImportedTextureStreamingRecord> _recordsByTexture = new();
    private readonly ConcurrentQueue<WeakReference<ImportedTextureStreamingRecord>> _recordRefs = new();
    private readonly object _recordRefsSnapshotSync = new();
    private WeakReference<ImportedTextureStreamingRecord>[] _recordRefsSnapshot = [];
    private int _recordRefsSnapshotDirty = 1;

    public bool IsEmpty => _recordRefs.IsEmpty;
    public int RecordReferenceCount => _recordRefs.Count;

    public bool IsTracked(XRTexture2D texture)
        => _recordsByTexture.TryGetValue(texture, out _);

    public ImportedTextureStreamingRecord GetOrCreateRecord(
        XRTexture2D texture,
        string? filePath,
        Func<ESizedInternalFormat, ITextureResidencyBackend> resolvePreviewBackend,
        bool deferAuthorityResolution = false)
    {
        ImportedTextureStreamingRecord record = _recordsByTexture.GetValue(texture, target =>
        {
            ImportedTextureStreamingRecord created = new(target);
            _recordRefs.Enqueue(new WeakReference<ImportedTextureStreamingRecord>(created));
            MarkRecordRefsSnapshotDirty();
            return created;
        });

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            lock (record.Sync)
            {
                record.Format = texture.SizedInternalFormat;
                if (!string.Equals(record.FilePath, filePath, StringComparison.OrdinalIgnoreCase) || record.Source is null)
                {
                    record.FilePath = filePath;
                    record.Source = deferAuthorityResolution
                        ? TextureStreamingSourceFactory.CreateDeferred(filePath)
                        : TextureStreamingSourceFactory.Create(filePath);
                }

                record.Backend ??= resolvePreviewBackend(record.Format);
            }
        }

        return record;
    }

    public void RecordUsage(XRMaterial? material, ImportedTextureStreamingUsage usage, long frameId)
    {
        if (material?.Textures is not { Count: > 0 } || frameId <= 0)
            return;

        float distance = usage.ClampedDistance;
        float projectedPixelSpan = usage.ClampedProjectedPixelSpan;
        float screenCoverage = usage.ClampedScreenCoverage;
        float uvDensityHint = usage.NormalizedUvDensityHint;
        SparseTextureStreamingPageSelection pageSelection = usage.NormalizedPageSelection;
        for (int textureIndex = 0; textureIndex < material.Textures.Count; textureIndex++)
        {
            if (material.Textures[textureIndex] is not XRTexture2D texture
                || !_recordsByTexture.TryGetValue(texture, out ImportedTextureStreamingRecord? record))
            {
                continue;
            }

            int writeVersion = BeginVisibilityWrite(record);
            try
            {
                if (record.LastVisibleFrameId != frameId)
                {
                    record.LastVisibleFrameId = frameId;
                    record.MinVisibleDistance = distance;
                    record.MaxProjectedPixelSpan = projectedPixelSpan;
                    record.MaxScreenCoverage = screenCoverage;
                    record.UvDensityHint = uvDensityHint;
                    record.VisiblePageSelection = pageSelection;
                }
                else
                {
                    if (distance < record.MinVisibleDistance)
                        record.MinVisibleDistance = distance;

                    if (projectedPixelSpan > record.MaxProjectedPixelSpan)
                        record.MaxProjectedPixelSpan = projectedPixelSpan;

                    if (screenCoverage > record.MaxScreenCoverage)
                        record.MaxScreenCoverage = screenCoverage;

                    if (uvDensityHint > record.UvDensityHint)
                        record.UvDensityHint = uvDensityHint;

                    record.VisiblePageSelection = record.VisiblePageSelection.Union(pageSelection);
                }
            }
            finally
            {
                EndVisibilityWrite(record, writeVersion);
            }

            if (TextureRuntimeDiagnostics.IsVerboseEnabled)
            {
                float priorityScore = (projectedPixelSpan + screenCoverage * 1024.0f)
                    * TextureResidencyPolicy.ResolveTextureRoleMultiplier(texture.SamplerName)
                    * uvDensityHint;
                TextureRuntimeDiagnostics.LogVisibilityRecorded(
                    frameId,
                    texture.Name,
                    record.FilePath,
                    projectedPixelSpan,
                    screenCoverage,
                    priorityScore);
            }
        }
    }

    public void RecordMaterialBinding(XRMaterialBase? material, long frameId)
    {
        if (material?.Textures is not { Count: > 0 } || IsEmpty || frameId <= 0)
            return;

        int materialTextureCount = material.Textures.Count;
        for (int textureIndex = 0; textureIndex < materialTextureCount; textureIndex++)
        {
            if (material.Textures[textureIndex] is not XRTexture2D texture
                || !_recordsByTexture.TryGetValue(texture, out ImportedTextureStreamingRecord? record))
            {
                continue;
            }

            int writeVersion = BeginVisibilityWrite(record);
            try
            {
                record.LastBoundFrameId = frameId;
                record.LastBoundMaterialTextureCount = materialTextureCount;
            }
            finally
            {
                EndVisibilityWrite(record, writeVersion);
            }
        }
    }

    public WeakReference<ImportedTextureStreamingRecord>[] GetRecordReferencesSnapshot()
    {
        if (Volatile.Read(ref _recordRefsSnapshotDirty) == 0)
            return _recordRefsSnapshot;

        lock (_recordRefsSnapshotSync)
        {
            if (_recordRefsSnapshotDirty != 0)
            {
                _recordRefsSnapshot = _recordRefs.ToArray();
                Volatile.Write(ref _recordRefsSnapshotDirty, 0);
            }

            return _recordRefsSnapshot;
        }
    }

    public void CompactRecordRefs()
    {
        int count = _recordRefs.Count;
        List<WeakReference<ImportedTextureStreamingRecord>> live = new(count);
        while (_recordRefs.TryDequeue(out WeakReference<ImportedTextureStreamingRecord>? refEntry))
        {
            if (refEntry.TryGetTarget(out ImportedTextureStreamingRecord? record)
                && record.Texture.TryGetTarget(out _))
            {
                live.Add(refEntry);
            }
        }

        foreach (WeakReference<ImportedTextureStreamingRecord> refEntry in live)
            _recordRefs.Enqueue(refEntry);

        MarkRecordRefsSnapshotDirty();
    }

    public List<ImportedTextureStreamingSnapshot> CollectSnapshots(
        Func<uint, uint, ESizedInternalFormat, ITextureResidencyBackend> resolveBackend)
    {
        List<ImportedTextureStreamingSnapshot> snapshots = [];
        CollectSnapshots(resolveBackend, snapshots);
        return snapshots;
    }

    public void CollectSnapshots(
        Func<uint, uint, ESizedInternalFormat, ITextureResidencyBackend> resolveBackend,
        List<ImportedTextureStreamingSnapshot> snapshots)
    {
        snapshots.Clear();
        WeakReference<ImportedTextureStreamingRecord>[] refs = GetRecordReferencesSnapshot();
        for (int i = 0; i < refs.Length; i++)
        {
            if (!refs[i].TryGetTarget(out ImportedTextureStreamingRecord? record)
                || !record.Texture.TryGetTarget(out XRTexture2D? texture))
            {
                continue;
            }

            if (!Monitor.TryEnter(record.Sync))
                continue;

            try
            {
                if (string.IsNullOrWhiteSpace(record.FilePath) || record.Source is null)
                    continue;

                ITextureResidencyBackend backend = record.Backend ?? resolveBackend(record.SourceWidth, record.SourceHeight, record.Format);

                ReadVisibilitySnapshot(
                    record,
                    out long lastVisibleFrameId,
                    out long lastBoundFrameId,
                    out int lastBoundMaterialTextureCount,
                    out float minVisibleDistance,
                    out float maxProjectedPixelSpan,
                    out float maxScreenCoverage,
                    out float uvDensityHint,
                    out SparseTextureStreamingPageSelection visiblePageSelection);

                snapshots.Add(new ImportedTextureStreamingSnapshot(
                    record,
                    backend,
                    texture.Name,
                    record.FilePath,
                    texture.SamplerName,
                    TextureResidencyPolicy.ResolveFairnessGroupKey(record.FilePath),
                    record.Format,
                    record.SourceWidth,
                    record.SourceHeight,
                    record.ResidentMaxDimension,
                    record.PendingMaxDimension,
                    record.ResidentGeneration,
                    record.PublishedGeneration,
                    record.UploadGeneration,
                    record.RetirementGeneration,
                    record.SparseNumLevels,
                    texture.SparseTextureStreamingEnabled
                        ? texture.SparseTextureStreamingCommittedBytes
                        : backend.EstimateCommittedBytes(record.SourceWidth, record.SourceHeight, record.ResidentMaxDimension, record.Format, record.SparseNumLevels),
                    texture.SparseTextureStreamingEnabled
                        ? texture.SparseTextureStreamingResidentPageSelection
                        : SparseTextureStreamingPageSelection.Full,
                    lastVisibleFrameId,
                    lastBoundFrameId,
                    lastBoundMaterialTextureCount,
                    minVisibleDistance,
                    maxProjectedPixelSpan,
                    maxScreenCoverage,
                    uvDensityHint,
                    visiblePageSelection,
                    record.PreviewReady,
                    record.LastTransitionFrameId,
                    record.PendingTransitionQueuedTimestamp,
                    record.LastTransitionQueueWaitMilliseconds,
                    record.LastTransitionExecutionMilliseconds,
                    record.LastPressureDemotionFrameId,
                    texture.TextureUploadValidationFailureCount,
                    texture.LastTextureQueueWaitMilliseconds,
                    texture.LastTextureUploadDurationMilliseconds,
                    record.FailedTransitionCooldownUntilFrameId,
                    record.FailedTransitionTargetMaxDimension,
                    record.ConsecutiveTransitionFailureCount));
            }
            finally
            {
                Monitor.Exit(record.Sync);
            }
        }
    }

    public int CountPendingTransitions()
    {
        int count = 0;
        WeakReference<ImportedTextureStreamingRecord>[] refs = GetRecordReferencesSnapshot();
        for (int i = 0; i < refs.Length; i++)
        {
            if (!refs[i].TryGetTarget(out ImportedTextureStreamingRecord? record)
                || !record.Texture.TryGetTarget(out _))
            {
                continue;
            }

            if (!Monitor.TryEnter(record.Sync))
            {
                count++;
                continue;
            }

            try
            {
                if (record.PendingMaxDimension != 0)
                    count++;
            }
            finally
            {
                Monitor.Exit(record.Sync);
            }
        }

        return count;
    }

    private static int BeginVisibilityWrite(ImportedTextureStreamingRecord record)
    {
        // Visibility mutations are a handful of scalar writes. A multi-writer
        // sequence lock keeps that bounded critical section in user mode instead
        // of parking the collect thread in Monitor.Enter.
        SpinWait spinWait = default;
        while (true)
        {
            int version = Volatile.Read(ref record.VisibilityVersion);
            if ((version & 1) != 0)
            {
                spinWait.SpinOnce();
                continue;
            }

            int writeVersion = unchecked(version + 1);
            if (Interlocked.CompareExchange(ref record.VisibilityVersion, writeVersion, version) == version)
                return writeVersion;

            spinWait.SpinOnce();
        }
    }

    private static void EndVisibilityWrite(ImportedTextureStreamingRecord record, int writeVersion)
        => Volatile.Write(ref record.VisibilityVersion, unchecked(writeVersion + 1));

    private static void ReadVisibilitySnapshot(
        ImportedTextureStreamingRecord record,
        out long lastVisibleFrameId,
        out long lastBoundFrameId,
        out int lastBoundMaterialTextureCount,
        out float minVisibleDistance,
        out float maxProjectedPixelSpan,
        out float maxScreenCoverage,
        out float uvDensityHint,
        out SparseTextureStreamingPageSelection visiblePageSelection)
    {
        SpinWait spinWait = default;
        while (true)
        {
            int version = Volatile.Read(ref record.VisibilityVersion);
            if ((version & 1) != 0)
            {
                spinWait.SpinOnce();
                continue;
            }

            lastVisibleFrameId = record.LastVisibleFrameId;
            lastBoundFrameId = record.LastBoundFrameId;
            lastBoundMaterialTextureCount = record.LastBoundMaterialTextureCount;
            minVisibleDistance = record.MinVisibleDistance;
            maxProjectedPixelSpan = record.MaxProjectedPixelSpan;
            maxScreenCoverage = record.MaxScreenCoverage;
            uvDensityHint = record.UvDensityHint;
            visiblePageSelection = record.VisiblePageSelection;

            if (version == Volatile.Read(ref record.VisibilityVersion))
                return;

            spinWait.SpinOnce();
        }
    }

    private void MarkRecordRefsSnapshotDirty()
        => Volatile.Write(ref _recordRefsSnapshotDirty, 1);
}
