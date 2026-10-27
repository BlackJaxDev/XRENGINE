using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Gaussian;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Components.Scene.Mesh;

// Loads a cache and collects the decoded data that the GPU does not have yet.
public partial class GaussianCacheComponent
{
    private readonly XRPropertyChangedEventHandler _chunkAcceptedHandler;
    private readonly object _uploadSync = new();
    private readonly List<CacheUpload> _pendingUploads = [];
    private UploadTracker? _tracker;
    private bool _trackerChanged;

    /// <summary>
    /// Loads the root asset of a cache, sets <see cref="Cache"/>, and then loads and accepts the chunks in order. The
    /// component uploads the new data of each chunk after the cache accepts it. A chunk path that is not rooted is
    /// relative to the folder of the root asset. The method stops without an error when <see cref="Cache"/> changes
    /// while it loads.
    /// </summary>
    /// <exception cref="FileNotFoundException">The root asset or a chunk could not be loaded.</exception>
    /// <exception cref="GaussianCacheFormatException">The cache or a chunk is not valid.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task LoadAsync(string rootAssetPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootAssetPath);
        cancellationToken.ThrowIfCancellationRequested();

        GaussianCache cache = await LoadAssetAsync<GaussianCache>(rootAssetPath, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"The gaussian cache '{rootAssetPath}' could not be loaded.", rootAssetPath);
        cancellationToken.ThrowIfCancellationRequested();
        Cache = cache;

        string? folder = Path.GetDirectoryName(rootAssetPath);
        while (!cache.IsComplete && ReferenceEquals(_cache, cache))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = cache.ChunkAssetPaths[cache.NextChunkIndex - 1];
            string chunkPath = string.IsNullOrEmpty(folder) || Path.IsPathRooted(relativePath)
                ? relativePath
                : Path.Combine(folder, relativePath);
            GaussianCacheChunk chunk = await LoadAssetAsync<GaussianCacheChunk>(chunkPath, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException($"The gaussian cache chunk '{chunkPath}' could not be loaded.", chunkPath);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_cache, cache))
                return;
            // The cache raises a NextChunkIndex change, and the handler collects the new data.
            cache.AcceptChunk(chunk);
        }
    }

    /// <summary>
    /// Loads an asset through the engine asset manager on a worker task. The rendering runtime gets only the
    /// synchronous load of the asset manager, so the worker task keeps the caller free.
    /// </summary>
    private static Task<T?> LoadAssetAsync<T>(string path, CancellationToken cancellationToken) where T : XRAsset, new()
        => Task.Run(() => RuntimeEngine.Assets.Load<T>(path), cancellationToken);

    /// <summary>Starts the uploads of a new cache. The first upload holds all data that the cache has decoded.</summary>
    private void QueueCacheUpload(GaussianCache? cache)
    {
        UploadTracker? tracker = null;
        if (cache is not null)
        {
            try
            {
                tracker = new UploadTracker(cache);
            }
            catch (Exception error)
            {
                Debug.RenderingException(error, $"Gaussian cache '{cache.Name}' cannot be played.");
            }
        }

        lock (_uploadSync)
        {
            _tracker = tracker;
            _trackerChanged = true;
            _pendingUploads.Clear();
        }

        if (tracker is not null)
            CollectUploads(tracker);
        else
            _renderInfo.LocalCullingVolume = null;
    }

    private void OnCacheChunkAccepted(object? sender, IXRPropertyChangedEventArgs args)
    {
        UploadTracker? tracker = Volatile.Read(ref _tracker);
        if (tracker is not null && ReferenceEquals(tracker.Cache, sender))
            CollectUploads(tracker);
    }

    /// <summary>Copies the data that the tracker has not uploaded yet, and queues it for the next PreRender call.</summary>
    private void CollectUploads(UploadTracker tracker)
    {
        CacheUpload upload;
        try
        {
            lock (tracker)
                upload = tracker.Collect();
        }
        catch (Exception error)
        {
            Debug.RenderingException(error, $"Failed to collect the decoded data of gaussian cache '{tracker.Cache.Name}'.");
            return;
        }

        lock (_uploadSync)
        {
            if (!ReferenceEquals(_tracker, tracker))
                return;
            _pendingUploads.Add(upload);
        }

        UpdateCullingVolume(tracker.Layout, upload);
    }

    /// <summary>
    /// Sets the local culling volume to the cache bounds, grown by the quad extent of the largest splat and by the
    /// largest offset of a splat from corner a of its driver triangle. The bounds hold every driver vertex in every
    /// frame. The offset is measured at frame 0 and grows by half, because motion can stretch a triangle.
    /// </summary>
    private void UpdateCullingVolume(CacheLayout layout, CacheUpload upload)
    {
        Vector3 margin = new((QuadExtentSigma * upload.MaxSigma) + (BindingOffsetStretch * upload.MaxBindingOffset));
        AABB bounds = layout.Bounds;
        _renderInfo.LocalCullingVolume = new AABB(bounds.Min - margin, bounds.Max + margin);
    }

    /// <summary>The sizes, offsets, and constants of one cache for the GPU buffers.</summary>
    private sealed class CacheLayout
    {
        public CacheLayout(GaussianCache cache)
        {
            GaussianCacheHeader header = cache.Header;
            GaussianCacheRuntimeData runtime = cache.Runtime;
            DriverFrameStore frames = runtime.Frames;
            int levelCount = header.SplatLevelCounts.Length;

            NodeOffsets = new int[levelCount];
            IndexOffsets = new int[levelCount];
            long nodes = 0;
            long indices = 0;
            for (int level = 0; level < levelCount; level++)
            {
                NodeOffsets[level] = checked((int)nodes);
                IndexOffsets[level] = checked((int)indices);
                nodes += header.SplatLevelCounts[level];
                if (level < runtime.DriverLevelIndices.Length)
                    indices += runtime.DriverLevelIndices[level].Length;
            }

            GridQuantizer grid = new(header.Bounds, header.GridBits);
            Header = header;
            LevelCount = levelCount;
            DriverLevelCount = frames.DriverLevelCount;
            TotalNodeCount = checked((int)nodes);
            TotalIndexCount = checked((int)indices);
            DriverVertexCount = frames.VertexCount;
            FrameCount = frames.FrameCount;
            FrameValueCount = frames.Positions.Length;
            GridOrigin = grid.Origin;
            GridCellSize = grid.CellSize;
            Bounds = header.Bounds;
        }

        public GaussianCacheHeader Header { get; }
        public int LevelCount { get; }
        public int DriverLevelCount { get; }
        public int TotalNodeCount { get; }
        public int TotalIndexCount { get; }
        public int DriverVertexCount { get; }
        public int FrameCount { get; }
        public int FrameValueCount { get; }
        public int[] NodeOffsets { get; }
        public int[] IndexOffsets { get; }
        public Vector3 GridOrigin { get; }
        public Vector3 GridCellSize { get; }
        public AABB Bounds { get; }
    }

    /// <summary>One range of new data for a GPU buffer.</summary>
    private readonly record struct UploadRange<T>(uint ElementOffset, T[] Data) where T : unmanaged;

    /// <summary>The new data of one collect call, copied so that the render thread can read it without a lock.</summary>
    private sealed class CacheUpload(UploadTracker tracker, long sequence)
    {
        public UploadTracker Tracker { get; } = tracker;
        public long Sequence { get; } = sequence;
        public List<UploadRange<GaussianCacheSplatRecord>> Records { get; } = [];
        public List<UploadRange<int>> Parents { get; } = [];
        public List<UploadRange<uint>> Indices { get; } = [];
        public List<UploadRange<ushort>> Frames { get; } = [];
        public GaussianCacheGpuLevel[] Levels { get; set; } = [];
        public long LoadedNodeCount { get; set; }
        public float MaxSigma { get; set; }
        public float MaxBindingOffset { get; set; }
    }

    /// <summary>
    /// Tracks which decoded data of one cache is already collected for upload. Lock the tracker to call
    /// <see cref="Collect"/>; the cache must not accept a chunk at the same time.
    /// </summary>
    private sealed class UploadTracker
    {
        private readonly int[] _collectedNodes;
        private readonly int[] _collectedTriangles;
        private readonly bool[] _collectedFrames;
        private long _sequence;
        private float _maxSigma;
        private float _maxBindingOffset;

        public UploadTracker(GaussianCache cache)
        {
            Cache = cache;
            Layout = new CacheLayout(cache);
            _collectedNodes = new int[Layout.LevelCount];
            _collectedTriangles = new int[Layout.DriverLevelCount];
            _collectedFrames = new bool[Layout.DriverLevelCount * Layout.FrameCount];
        }

        public GaussianCache Cache { get; }
        public CacheLayout Layout { get; }

        public CacheUpload Collect()
        {
            GaussianCacheRuntimeData runtime = Cache.Runtime;
            CacheUpload upload = new(this, ++_sequence);
            CollectFrames(runtime, upload);
            CollectTriangles(runtime, upload);
            CollectNodes(runtime, upload);

            GaussianCacheGpuLevel[] levels = new GaussianCacheGpuLevel[Layout.LevelCount];
            GaussianCacheGpuLevel.Fill(Cache, levels);
            upload.Levels = levels;
            long loaded = 0;
            for (int level = 0; level < Layout.LevelCount; level++)
                loaded += runtime.GetLoadedNodeCount(level);
            upload.LoadedNodeCount = loaded;
            upload.MaxSigma = _maxSigma;
            upload.MaxBindingOffset = _maxBindingOffset;
            return upload;
        }

        /// <summary>Collects each (driver level, frame) range that is decoded now. Adjacent ranges become one range.</summary>
        private void CollectFrames(GaussianCacheRuntimeData runtime, CacheUpload upload)
        {
            DriverFrameStore frames = runtime.Frames;
            int rangeStart = -1;
            int rangeEnd = -1;
            for (int frame = 0; frame < Layout.FrameCount; frame++)
            {
                for (int level = 0; level < Layout.DriverLevelCount; level++)
                {
                    int count = frames.GetLevelVertexCount(level);
                    int flag = (level * Layout.FrameCount) + frame;
                    if (count == 0 || _collectedFrames[flag])
                        continue;
                    int first = frames.GetLevelFirstVertex(level);
                    if (!frames.IsDecoded(frame, first, count))
                        continue;

                    _collectedFrames[flag] = true;
                    int start = ((frame * frames.VertexCount) + first) * 3;
                    int end = start + (count * 3);
                    if (start == rangeEnd)
                    {
                        rangeEnd = end;
                        continue;
                    }

                    AddFrameRange(frames, upload, rangeStart, rangeEnd);
                    rangeStart = start;
                    rangeEnd = end;
                }
            }

            AddFrameRange(frames, upload, rangeStart, rangeEnd);
        }

        private static void AddFrameRange(DriverFrameStore frames, CacheUpload upload, int start, int end)
        {
            if (start < 0 || end <= start)
                return;
            upload.Frames.Add(new UploadRange<ushort>((uint)start, frames.Positions.AsSpan(start, end - start).ToArray()));
        }

        private void CollectTriangles(GaussianCacheRuntimeData runtime, CacheUpload upload)
        {
            for (int level = 0; level < Layout.DriverLevelCount; level++)
            {
                int loaded = runtime.GetLoadedTriangleCount(level);
                int collected = _collectedTriangles[level];
                if (loaded <= collected)
                    continue;

                uint[] indices = runtime.DriverLevelIndices[level];
                upload.Indices.Add(new UploadRange<uint>(
                    (uint)(Layout.IndexOffsets[level] + (collected * 3)),
                    indices.AsSpan(collected * 3, (loaded - collected) * 3).ToArray()));
                _collectedTriangles[level] = loaded;
            }
        }

        private void CollectNodes(GaussianCacheRuntimeData runtime, CacheUpload upload)
        {
            for (int level = 0; level < Layout.LevelCount; level++)
            {
                int loaded = runtime.GetLoadedNodeCount(level);
                int collected = _collectedNodes[level];
                if (loaded <= collected)
                    continue;

                uint offset = (uint)(Layout.NodeOffsets[level] + collected);
                GaussianCacheSplatRecord[] records = runtime.LevelRecords[level].AsSpan(collected, loaded - collected).ToArray();
                upload.Records.Add(new UploadRange<GaussianCacheSplatRecord>(offset, records));
                upload.Parents.Add(new UploadRange<int>(offset, runtime.ParentIndex[level].AsSpan(collected, loaded - collected).ToArray()));
                _collectedNodes[level] = loaded;

                foreach (GaussianCacheSplatRecord record in records)
                {
                    _maxSigma = MathF.Max(_maxSigma, MathF.Max(record.ScaleX, MathF.Max(record.ScaleY, record.ScaleZ)));
                    if ((record.Flags & GaussianCacheSplatRecord.StaticFlag) == 0)
                        _maxBindingOffset = MathF.Max(_maxBindingOffset, BindingOffset(runtime, level, record));
                }
            }
        }

        /// <summary>
        /// Returns a bound on the distance from corner a of the driver triangle to the splat at frame 0:
        /// |u| |b − a| + |v| |c − a| + |h|. Returns 0 when frame 0 of the triangle is not decoded yet.
        /// </summary>
        private float BindingOffset(GaussianCacheRuntimeData runtime, int level, in GaussianCacheSplatRecord record)
        {
            DriverFrameStore frames = runtime.Frames;
            uint[] indices = runtime.DriverLevelIndices[level];
            int ia = (int)indices[record.Triangle * 3];
            int ib = (int)indices[record.Triangle * 3 + 1];
            int ic = (int)indices[record.Triangle * 3 + 2];
            if (!frames.IsDecoded(0, ia, 1) || !frames.IsDecoded(0, ib, 1) || !frames.IsDecoded(0, ic, 1))
                return 0.0f;

            Vector3 a = RestPosition(frames, ia);
            float u = PlaneCoordinate.Unpack(record.A);
            float v = PlaneCoordinate.Unpack(record.B);
            float h = (float)BitConverter.UInt16BitsToHalf(record.C);
            return (MathF.Abs(u) * Vector3.Distance(RestPosition(frames, ib), a))
                + (MathF.Abs(v) * Vector3.Distance(RestPosition(frames, ic), a))
                + MathF.Abs(h);
        }

        private Vector3 RestPosition(DriverFrameStore frames, int vertex)
        {
            ushort[] positions = frames.Positions;
            int offset = vertex * 3;
            return Layout.GridOrigin + (new Vector3(positions[offset], positions[offset + 1], positions[offset + 2]) * Layout.GridCellSize);
        }
    }
}
