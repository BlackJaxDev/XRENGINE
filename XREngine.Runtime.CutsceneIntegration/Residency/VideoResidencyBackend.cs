using System.Text;
using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Cutscenes;

/// <summary>
/// A residency backend for the video clip sources of a cutscene. The asset of a source file has the key
/// <see cref="CutsceneResidencyAssetKeys.ForVideoSource"/>. Each source has one media session and one
/// <see cref="VideoFrameCache"/>; a video track copies a frame from that cache on a miss of its own cache, with no decode.
/// </summary>
/// <remarks>
/// <para>
/// The backend keeps only a small warm set for each source (<see cref="SourceCacheCapacity"/> frames and at most
/// <see cref="SourceCacheBudgetBytes"/> bytes), so a source is not cached two times.
/// </para>
/// <para>
/// T0 opens the media session of the source and decodes the keyframe at or before the file time of the deadline. It stays
/// resident while the cutscene is open. T1 decodes the keyframe at or before its deadline. T2 decodes the exact frame of its
/// deadline and the <see cref="WarmFrameCount"/> frames after it, so the cache holds the frames around the playhead. The
/// file time of a deadline is the file time of the clip that shows at the deadline, or the first frame of the next clip.
/// All decoding runs on background tasks and on the decoder thread of the session. The loads of one source run one at a time.
/// </para>
/// <para>
/// The backend follows the contract of <see cref="IResidencyBackend"/>: it sets the level of a ticket before it calls
/// <see cref="CutsceneResidencyPlanner.NotifyCompleted"/>. A cancel of a complete ticket releases its frames from the cache,
/// unless another complete ticket holds the same frame. The session of a source closes when no ticket of the source is left.
/// A cancel of running work makes the task drop its frames at its end and report nothing.
/// </para>
/// <para>
/// <see cref="BuildManifest"/> makes the manifest of the video planner from the timeline: each range of a video clip needs
/// T2 of its source, with the screen coverage one, because the bake does not see video surfaces.
/// </para>
/// </remarks>
public sealed class VideoResidencyBackend : IResidencyBackend, IDisposable
{
    /// <summary>The number of frames after the exact frame that a T2 load decodes.</summary>
    public const int WarmFrameCount = 2;

    /// <summary>
    /// The number of frames that the warm set of each source holds. The set is small, because the track keeps its own
    /// cache: the backend holds only the frames that a jump needs first.
    /// </summary>
    public const int SourceCacheCapacity = 4;

    /// <summary>The byte budget of the warm set of each source: 16 MiB (two 1080p frames, or one 4K frame).</summary>
    public const long SourceCacheBudgetBytes = 16L * 1024 * 1024;

    /// <summary>The number of frames that the media session of a source queues. A load takes each frame at once.</summary>
    public const int SourceSessionQueueCapacity = 1;

    /// <summary>The longest time that one load waits for the session, in seconds.</summary>
    public const double LoadTimeoutSeconds = 10.0;

    private static readonly Func<object?, Task> LoadAction = static state => ((BackendTicket)state!).Owner.LoadAsync((BackendTicket)state);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // A clock that releases the first queued frame whatever its time.
    private const long ReleaseAnyFrame = long.MaxValue / 4;

    private readonly Func<IMediaStreamSession> _createSession;
    private readonly TaskScheduler _scheduler;
    private readonly CutsceneTime _duration;
    private readonly Dictionary<Guid, VideoSourceEntry> _sources = [];
    private readonly Dictionary<string, VideoSourceEntry> _sourcesByPath = new(PathComparer);
    private readonly List<VideoSourceEntry> _sourceOrder = [];
    private readonly Lock _sync = new();
    private readonly Dictionary<long, BackendTicket> _tickets = [];
    private CutsceneResidencyPlanner? _planner;
    private long _nextTicketId;
    private bool _disposed;
    private int _rejectedRequestCount;
    private int _failedLoadCount;

    /// <summary>Creates a backend for the video clip sources of a cutscene.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="createSession">Creates a media session, or null for <see cref="VideoTrackEvaluator.CreateDefaultSession"/>.</param>
    public VideoResidencyBackend(CompiledCutscene compiled, Func<IMediaStreamSession>? createSession = null)
        : this(compiled, createSession, TaskScheduler.Default)
    {
    }

    /// <summary>Creates a backend with a scheduler for the load tasks.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="createSession">Creates a media session, or null for the default.</param>
    /// <param name="scheduler">The scheduler of the load tasks.</param>
    internal VideoResidencyBackend(CompiledCutscene compiled, Func<IMediaStreamSession>? createSession, TaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(scheduler);
        _createSession = createSession ?? VideoTrackEvaluator.CreateDefaultSession;
        _scheduler = scheduler;
        _duration = compiled.Asset.Duration;
        CollectSources(compiled.Asset);
    }

    /// <summary>The number of video source files.</summary>
    public int SourceCount => _sourceOrder.Count;

    /// <summary>The number of requests that the backend did not accept.</summary>
    public int RejectedRequestCount => Volatile.Read(ref _rejectedRequestCount);

    /// <summary>The number of loads that failed to open the session, to seek, or to get a frame.</summary>
    public int FailedLoadCount => Volatile.Read(ref _failedLoadCount);

    /// <summary>The number of sources that have an open media session now.</summary>
    public int OpenSessionCount
    {
        get
        {
            lock (_sync)
            {
                int count = 0;
                for (int i = 0; i < _sourceOrder.Count; i++)
                    if (_sourceOrder[i].Session is not null)
                        count++;
                return count;
            }
        }
    }

    /// <summary>The frame cache of a source, or null for an unknown file.</summary>
    /// <param name="fullPath">The full path of the source file.</param>
    public VideoFrameCache? GetCache(string fullPath)
        => _sourcesByPath.TryGetValue(fullPath, out VideoSourceEntry? source) ? source.Cache : null;

    /// <summary>
    /// Copies the frame that shows at a stream time from the cache of a source into another cache, with no decode.
    /// The update thread can call it while loads run.
    /// </summary>
    /// <param name="fullPath">The full path of the source file.</param>
    /// <param name="timeTicks">The stream time in 100 ns ticks.</param>
    /// <param name="target">The cache that receives the copy.</param>
    /// <param name="scratch">A buffer for the copy. It grows when it is too small.</param>
    /// <returns>True if the source cache had the frame.</returns>
    public bool TryCopyFrame(string fullPath, long timeTicks, VideoFrameCache target, ref byte[]? scratch)
        => _sourcesByPath.TryGetValue(fullPath, out VideoSourceEntry? source) && source.Cache.TryCopyFrameTo(timeTicks, target, ref scratch);

    /// <summary>
    /// Builds the manifest of the video planner. Each source is one <see cref="EResidencyAssetKind.Video"/> asset. The
    /// buckets split the timeline at each clip edge, and each range of a clip needs T2 of its source, with the coverage one.
    /// </summary>
    public CutsceneResidencyManifest BuildManifest()
    {
        CutsceneResidencyManifestBuilder builder = new();
        long duration = _duration.Flicks;
        SortedSet<long> edges = [];
        if (duration > 0)
        {
            edges.Add(0L);
            edges.Add(duration);
        }
        for (int s = 0; s < _sourceOrder.Count; s++)
        {
            VideoSourceEntry source = _sourceOrder[s];
            builder.AddAsset(source.AssetId, EResidencyAssetKind.Video, 0L);
            for (int c = 0; c < source.Clips.Count; c++)
            {
                (long start, long end) = source.Clips[c].GetRange(duration);
                if (end <= start)
                    continue;
                edges.Add(start);
                edges.Add(end);
            }
        }

        long previous = long.MinValue;
        foreach (long edge in edges)
        {
            if (previous != long.MinValue)
                AddBucket(builder, previous, edge, duration);
            previous = edge;
        }
        return builder.Build();
    }

    /// <inheritdoc />
    public void AttachPlanner(CutsceneResidencyPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        lock (_sync)
            _planner = planner;
    }

    /// <inheritdoc />
    public bool CanResolve(Guid assetId)
        => _sources.ContainsKey(assetId);

    /// <inheritdoc />
    /// <remarks>Each level starts a task that opens the session if it is not open, seeks, and decodes the frames of the level.</remarks>
    public ResidencyTicket Request(in ResidencyRequest request)
    {
        if (!_sources.TryGetValue(request.AssetId, out VideoSourceEntry? source))
            return Reject(request, "the asset is not a video source of the cutscene");
        if (request.TargetLevel is < (int)EResidencyLevel.Proxy or > (int)EResidencyLevel.Target)
            return Reject(request, "the level must be T0, T1, or T2");

        BackendTicket ticket;
        lock (_sync)
        {
            if (_disposed)
                return ResidencyTicket.None;

            ticket = new BackendTicket(this, ++_nextTicketId, source, request.TargetLevel, request.Deadline);
            _tickets.Add(ticket.Id, ticket);
            source.TicketCount++;
        }

        Task.Factory.StartNew(LoadAction, ticket, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _scheduler).Unwrap();
        return new ResidencyTicket(ticket.Id);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A cancel of a complete ticket removes its frames from the cache, unless another complete ticket of the source holds the
    /// same frame. When no ticket of the source is left, its session closes in the background.
    /// </remarks>
    public void Cancel(ResidencyTicket ticket)
    {
        if (!ticket.IsValid)
            return;

        lock (_sync)
        {
            if (!_tickets.Remove(ticket.Id, out BackendTicket? entry))
                return;

            entry.Cancelled = true;
            ReleaseFrames(entry);
            ReleaseTicket(entry.Source);
        }
    }

    /// <inheritdoc />
    /// <remarks>The level is the highest level of a complete ticket of the asset.</remarks>
    public EResidencyLevel GetLevel(Guid assetId)
    {
        lock (_sync)
        {
            int level = (int)EResidencyLevel.None;
            foreach (BackendTicket ticket in _tickets.Values)
                if (ticket.Complete && ticket.Source.AssetId == assetId && ticket.Level > level)
                    level = ticket.Level;
            return (EResidencyLevel)level;
        }
    }

    /// <summary>Cancels all tickets, clears the caches, and closes the sessions. A running task drops its frames at its end.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _planner = null;
            foreach (BackendTicket ticket in _tickets.Values)
                ticket.Cancelled = true;
            _tickets.Clear();
            for (int i = 0; i < _sourceOrder.Count; i++)
            {
                VideoSourceEntry source = _sourceOrder[i];
                source.TicketCount = 0;
                source.Cache.Clear();
                CloseSessionLocked(source);
            }
        }
    }

    /// <summary>
    /// Gets the inputs of the backend of an asset as one string: the folder of the asset file, the duration, and each video
    /// clip (track, source path, start, duration, offset, speed). Two compiles with the same string give the same backend.
    /// </summary>
    /// <param name="asset">The cutscene asset.</param>
    internal static string GetInputSignature(CutsceneAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        StringBuilder text = new();
        text.Append(Path.GetDirectoryName(asset.FilePath)).Append('|').Append(asset.Duration.Flicks);
        for (int t = 0; t < asset.Tracks.Count; t++)
        {
            if (asset.Tracks[t] is not VideoTrack track)
                continue;
            for (int c = 0; c < track.Clips.Count; c++)
            {
                if (track.Clips[c] is not VideoTrackClip clip)
                    continue;
                text.Append("|v:").Append(t).Append(',').Append(clip.SourcePath).Append(',').Append(clip.Start.Flicks)
                    .Append(',').Append(clip.Duration.Flicks).Append(',').Append(clip.ClipOffset.Flicks).Append(',').Append(clip.Speed);
            }
        }
        return text.ToString();
    }

    /// <summary>True if the asset has a video clip with a source path.</summary>
    /// <param name="asset">The cutscene asset.</param>
    internal static bool HasVideoClips(CutsceneAsset asset)
    {
        for (int t = 0; t < asset.Tracks.Count; t++)
        {
            if (asset.Tracks[t] is not VideoTrack track)
                continue;
            for (int c = 0; c < track.Clips.Count; c++)
                if (track.Clips[c] is VideoTrackClip clip && !string.IsNullOrWhiteSpace(clip.SourcePath))
                    return true;
        }
        return false;
    }

    private async Task LoadAsync(BackendTicket ticket)
    {
        VideoSourceEntry source = ticket.Source;
        if (IsCancelled(ticket))
            return;

        List<long> frames = [];
        Exception? failure = null;
        await source.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsCancelled(ticket))
            {
                IMediaStreamSession session = await GetSessionAsync(source).ConfigureAwait(false);
                long ticks = source.GetFileTicks(ticket.Deadline);
                EVideoSeekMode mode = ticket.Level == (int)EResidencyLevel.Target ? EVideoSeekMode.Exact : EVideoSeekMode.NearestKeyframe;
                await session.SeekAsync(ticks, mode, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(LoadTimeoutSeconds)).ConfigureAwait(false);
                int count = mode == EVideoSeekMode.Exact ? 1 + WarmFrameCount : 1;
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                while (frames.Count < count && !IsCancelled(ticket))
                {
                    if (session.TryDequeueVideoFrame(ReleaseAnyFrame, out DecodedVideoFrame frame))
                    {
                        source.Cache.Insert(frame);
                        frames.Add(frame.PresentationTimestampTicks);
                        continue;
                    }

                    // The end of the file can come before the warm frames. The first frame is the level itself.
                    if (watch.Elapsed.TotalSeconds > (frames.Count == 0 ? LoadTimeoutSeconds : 0.5))
                        break;
                    await Task.Delay(1).ConfigureAwait(false);
                }
                if (frames.Count == 0 && !IsCancelled(ticket))
                    throw new TimeoutException("the session gave no frame after the seek");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex;
        }
        finally
        {
            source.Gate.Release();
        }

        CutsceneResidencyPlanner? planner = null;
        bool cancelled;
        lock (_sync)
        {
            ticket.Frames = frames;
            cancelled = ticket.Cancelled || _disposed;
            if (cancelled)
            {
                // A cancel of running work drops its frames and reports nothing. A close of the session by that cancel can
                // also fail the load; that is not a failure.
                if (!_disposed)
                    ReleaseFrames(ticket);
            }
            else if (failure is null)
            {
                // The level is set before the planner hears of the completion.
                ticket.Complete = true;
                planner = _planner;
            }
            else
            {
                ReleaseFrames(ticket);
                if (_tickets.Remove(ticket.Id))
                    ReleaseTicket(source);
            }
        }

        if (cancelled)
            return;
        if (failure is not null)
        {
            Interlocked.Increment(ref _failedLoadCount);
            Debug.LogWarning(ELogCategory.Animation, "Cutscene video residency: level T{0} of '{1}' did not load: {2}", ticket.Level, source.FullPath, failure.Message);
            return;
        }
        planner?.NotifyCompleted(new ResidencyTicket(ticket.Id));
    }

    private async Task<IMediaStreamSession> GetSessionAsync(VideoSourceEntry source)
    {
        lock (_sync)
        {
            if (source.Session is { } open)
                return open;
        }

        IMediaStreamSession session = _createSession();
        try
        {
            StreamOpenOptions options = new()
            {
                ClockDrivenPresentation = true,
                DecodeAudio = false,
                VideoQueueCapacity = SourceSessionQueueCapacity,
                EnableReconnect = false,
            };
            await session.OpenAsync(source.FullPath, options, CancellationToken.None).ConfigureAwait(false);
            if (!session.CanSeek)
                throw new NotSupportedException("the source does not support a seek (a live stream, a network stream, or an HLS playlist)");
        }
        catch
        {
            CloseInBackground(session);
            throw;
        }

        lock (_sync)
        {
            // No ticket is left, or the backend closed: the session is not kept.
            if (_disposed || source.TicketCount == 0)
            {
                CloseInBackground(session);
                throw new OperationCanceledException("the source has no ticket");
            }
            source.Session = session;
            return session;
        }
    }

    private bool IsCancelled(BackendTicket ticket)
    {
        lock (_sync)
            return ticket.Cancelled || _disposed;
    }

    // Call with _sync held. Removes the frames of a ticket that no other complete ticket of the source holds.
    private void ReleaseFrames(BackendTicket ticket)
    {
        List<long>? frames = ticket.Frames;
        ticket.Frames = null;
        if (frames is null)
            return;

        for (int f = 0; f < frames.Count; f++)
        {
            long pts = frames[f];
            bool held = false;
            foreach (BackendTicket other in _tickets.Values)
            {
                if (other.Complete && ReferenceEquals(other.Source, ticket.Source) && other.Frames is { } otherFrames && otherFrames.Contains(pts))
                {
                    held = true;
                    break;
                }
            }
            if (!held)
                ticket.Source.Cache.Remove(pts);
        }
    }

    // Call with _sync held. The session of a source with no ticket closes.
    private static void ReleaseTicket(VideoSourceEntry source)
    {
        source.TicketCount = Math.Max(0, source.TicketCount - 1);
        if (source.TicketCount == 0)
            CloseSessionLocked(source);
    }

    private static void CloseSessionLocked(VideoSourceEntry source)
    {
        if (source.Session is not { } session)
            return;
        source.Session = null;
        CloseInBackground(session);
    }

    // A close joins the decode thread, so it runs off the update thread.
    private static void CloseInBackground(IMediaStreamSession session)
        => Task.Run(() =>
        {
            session.Close();
            session.Dispose();
        });

    private ResidencyTicket Reject(in ResidencyRequest request, string reason)
    {
        Interlocked.Increment(ref _rejectedRequestCount);
        Debug.LogWarning(ELogCategory.Animation, "Cutscene video residency: the request for asset {0} at level T{1} (generation {2}) was not accepted: {3}.", request.AssetId, request.TargetLevel, request.Generation, reason);
        return ResidencyTicket.None;
    }

    private void CollectSources(CutsceneAsset asset)
    {
        string? assetDirectory = Path.GetDirectoryName(asset.FilePath);
        for (int t = 0; t < asset.Tracks.Count; t++)
        {
            if (asset.Tracks[t] is not VideoTrack track)
                continue;

            for (int c = 0; c < track.Clips.Count; c++)
            {
                if (track.Clips[c] is not VideoTrackClip clip || string.IsNullOrWhiteSpace(clip.SourcePath))
                    continue;

                string fullPath = AudioTrackEvaluator.ResolveSourcePath(clip.SourcePath, assetDirectory);
                if (!_sourcesByPath.TryGetValue(fullPath, out VideoSourceEntry? source))
                {
                    source = new VideoSourceEntry(CutsceneResidencyAssetKeys.ForVideoSource(fullPath), fullPath);
                    if (!_sources.TryAdd(source.AssetId, source))
                        continue;
                    _sourcesByPath.Add(fullPath, source);
                    _sourceOrder.Add(source);
                }
                source.Clips.Add(new VideoClipRange(clip.Start, clip.Duration, clip.ClipOffset, clip.Speed));
            }
        }
    }

    private void AddBucket(CutsceneResidencyManifestBuilder builder, long start, long end, long duration)
    {
        builder.BeginBucket(new CutsceneTime(start), new CutsceneTime(end), startsAtCut: false);
        for (int s = 0; s < _sourceOrder.Count; s++)
        {
            List<VideoClipRange> clips = _sourceOrder[s].Clips;
            for (int c = 0; c < clips.Count; c++)
            {
                (long clipStart, long clipEnd) = clips[c].GetRange(duration);
                if (clipStart < end && clipEnd > start)
                {
                    builder.AddEntry(s, (int)EResidencyLevel.Target, 1.0f);
                    break;
                }
            }
        }
        builder.EndBucket();
    }

    /// <summary>One video clip of a source: its range on the timeline, its offset in the file, and its speed.</summary>
    private readonly record struct VideoClipRange(CutsceneTime Start, CutsceneTime Duration, CutsceneTime ClipOffset, double Speed)
    {
        public (long Start, long End) GetRange(long timelineDuration)
        {
            long start = Math.Clamp(Start.Flicks, 0L, timelineDuration);
            long end = Math.Clamp((Start + Duration).Flicks, 0L, timelineDuration);
            return (start, end);
        }
    }

    /// <summary>One source file: its clips, its frame cache, and its media session.</summary>
    private sealed class VideoSourceEntry(Guid assetId, string fullPath)
    {
        public Guid AssetId { get; } = assetId;
        public string FullPath { get; } = fullPath;
        public List<VideoClipRange> Clips { get; } = [];
        public VideoFrameCache Cache { get; } = new(SourceCacheCapacity, SourceCacheBudgetBytes);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public IMediaStreamSession? Session { get; set; }
        public int TicketCount { get; set; }

        // The file time of the clip that shows at the deadline. Before a clip, the start of the next clip. After all clips,
        // the start of the last clip.
        public long GetFileTicks(CutsceneTime deadline)
        {
            CutsceneTime? next = null;
            CutsceneTime nextStart = default;
            CutsceneTime last = default;
            CutsceneTime lastStart = default;
            for (int i = 0; i < Clips.Count; i++)
            {
                VideoClipRange clip = Clips[i];
                CutsceneTime end = clip.Start + clip.Duration;
                if (deadline >= clip.Start && deadline < end)
                    return ToTicks(clip.ClipOffset) + (long)((deadline - clip.Start).ToSamples((int)TimeSpan.TicksPerSecond) * clip.Speed);
                if (clip.Start > deadline && (next is null || clip.Start < nextStart))
                {
                    next = clip.ClipOffset;
                    nextStart = clip.Start;
                }
                if (i == 0 || clip.Start > lastStart)
                {
                    lastStart = clip.Start;
                    last = clip.ClipOffset;
                }
            }
            return ToTicks(next ?? last);
        }

        private static long ToTicks(CutsceneTime time)
            => time.ToSamples((int)TimeSpan.TicksPerSecond);
    }

    /// <summary>One request of the planner. A complete ticket holds the frames that it decoded.</summary>
    private sealed class BackendTicket(VideoResidencyBackend owner, long id, VideoSourceEntry source, int level, CutsceneTime deadline)
    {
        public VideoResidencyBackend Owner { get; } = owner;
        public long Id { get; } = id;
        public VideoSourceEntry Source { get; } = source;
        public int Level { get; } = level;
        public CutsceneTime Deadline { get; } = deadline;
        public bool Complete { get; set; }
        public bool Cancelled { get; set; }
        public List<long>? Frames { get; set; }
    }
}
