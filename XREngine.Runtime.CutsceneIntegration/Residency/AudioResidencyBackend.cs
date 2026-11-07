using System.Text;
using XREngine.Data;
using XREngine.Rendering.Residency;

namespace XREngine.Cutscenes;

/// <summary>
/// A residency backend for the audio clip sources of a cutscene. The asset of a source is its
/// <see cref="AudioProxyAsset"/>, with the key <see cref="CutsceneResidencyAssetKeys.ForAudioSource"/>.
/// </summary>
/// <remarks>
/// <para>
/// T0 is the proxy. It is part of the cutscene asset, so it is resident at once and the planner keeps it while the cutscene
/// is open. A stale proxy (its source hash does not match the file) never plays, so the backend does not accept T0 for it;
/// T1 and T2 of its source still load. T1 and T2 open the full decoder of the source file on a background task and seek it near the deadline position:
/// the file frame of the clip that plays at the deadline, or the first frame of the next clip. The ticket holds the open
/// decoder until the planner cancels it, so the data of the file is in the cache of the operating system before the
/// playhead gets there. The track evaluator opens its own decoder for playback.
/// </para>
/// <para>
/// The backend follows the contract of <see cref="IResidencyBackend"/>: it sets the level of a ticket before it calls
/// <see cref="CutsceneResidencyPlanner.NotifyCompleted"/>, and a cancel of a complete ticket releases its decoder.
/// A cancel of running work makes the task close its decoder at its end and report nothing.
/// </para>
/// <para>
/// <see cref="BuildManifest"/> makes the manifest of the audio planner from the timeline: audio has no visibility, so each
/// range of an audio clip with a proxy needs T2 of its source.
/// </para>
/// </remarks>
public sealed class AudioResidencyBackend : IResidencyBackend, IDisposable
{
    private static readonly Action<object?> LoadAction = static state => ((BackendTicket)state!).Owner.Load((BackendTicket)state);

    private readonly Func<string, IStreamingAudioDecoder> _openDecoder;
    private readonly TaskScheduler _scheduler;
    private readonly CutsceneTime _duration;
    private readonly Dictionary<Guid, AudioSourceEntry> _sources = [];
    private readonly List<AudioSourceEntry> _sourceOrder = [];
    private readonly Lock _sync = new();
    private readonly Dictionary<long, BackendTicket> _tickets = [];
    private CutsceneResidencyPlanner? _planner;
    private long _nextTicketId;
    private bool _disposed;
    private int _rejectedRequestCount;
    private int _failedLoadCount;

    /// <summary>Creates a backend for the audio clip sources of a cutscene that have a proxy.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="openDecoder">Opens a decoder for a full file path, or null for <see cref="AudioTrackEvaluator.OpenDefaultDecoder"/>.</param>
    public AudioResidencyBackend(CompiledCutscene compiled, Func<string, IStreamingAudioDecoder>? openDecoder = null)
        : this(compiled, openDecoder, TaskScheduler.Default)
    {
    }

    /// <summary>Creates a backend with a scheduler for the load tasks. A test runs the scheduler by hand.</summary>
    /// <param name="compiled">The compiled cutscene.</param>
    /// <param name="openDecoder">Opens a decoder for a full file path, or null for the default decoders.</param>
    /// <param name="scheduler">The scheduler of the load tasks.</param>
    internal AudioResidencyBackend(CompiledCutscene compiled, Func<string, IStreamingAudioDecoder>? openDecoder, TaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(scheduler);
        _openDecoder = openDecoder ?? AudioTrackEvaluator.OpenDefaultDecoder;
        _scheduler = scheduler;
        _duration = compiled.Asset.Duration;
        CollectSources(compiled.Asset);
    }

    /// <summary>The number of audio sources with a proxy.</summary>
    public int SourceCount => _sourceOrder.Count;

    /// <summary>The number of requests that the backend did not accept, because the asset is not an audio source with a proxy.</summary>
    public int RejectedRequestCount => Volatile.Read(ref _rejectedRequestCount);

    /// <summary>The number of T1 and T2 loads that failed to open or seek the full decoder.</summary>
    public int FailedLoadCount => Volatile.Read(ref _failedLoadCount);

    /// <summary>The number of tickets that hold an open full decoder now.</summary>
    public int OpenDecoderCount
    {
        get
        {
            lock (_sync)
            {
                int count = 0;
                foreach (BackendTicket ticket in _tickets.Values)
                    if (ticket.Decoder is not null)
                        count++;
                return count;
            }
        }
    }

    /// <summary>
    /// Builds the manifest of the audio planner. Each source with a proxy is one <see cref="EResidencyAssetKind.Audio"/>
    /// asset, with the length of its encoded data as its T0 size. The buckets split the timeline at each clip edge, and each
    /// range of a clip needs T2 of its source, with the screen coverage one.
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
            AudioSourceEntry source = _sourceOrder[s];
            builder.AddAsset(source.AssetId, EResidencyAssetKind.Audio, source.Proxy.EncodedData.Length);
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
    /// <remarks>
    /// T0 completes in the call, because the proxy is in memory. T1 and T2 start a task that opens and seeks the full decoder.
    /// </remarks>
    public ResidencyTicket Request(in ResidencyRequest request)
    {
        if (!_sources.TryGetValue(request.AssetId, out AudioSourceEntry? source))
            return Reject(request, "the asset is not an audio source with a proxy");
        if (request.TargetLevel is < (int)EResidencyLevel.Proxy or > (int)EResidencyLevel.Target)
            return Reject(request, "the level must be T0, T1, or T2");
        // The track evaluator already gives the one diagnostic for a stale proxy, so this rejection logs nothing.
        if (request.TargetLevel == (int)EResidencyLevel.Proxy && !source.ProxyIsCurrent)
        {
            Interlocked.Increment(ref _rejectedRequestCount);
            return ResidencyTicket.None;
        }

        BackendTicket ticket;
        CutsceneResidencyPlanner? planner;
        lock (_sync)
        {
            if (_disposed)
                return ResidencyTicket.None;

            ticket = new BackendTicket(this, ++_nextTicketId, source, request.TargetLevel, request.Deadline);
            _tickets.Add(ticket.Id, ticket);
            if (request.TargetLevel != (int)EResidencyLevel.Proxy)
            {
                Task.Factory.StartNew(LoadAction, ticket, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _scheduler);
                return new ResidencyTicket(ticket.Id);
            }

            // The proxy is part of the asset, so the level is set before the planner hears of the completion.
            ticket.Complete = true;
            planner = _planner;
        }

        planner?.NotifyCompleted(new ResidencyTicket(ticket.Id));
        return new ResidencyTicket(ticket.Id);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A cancel of a complete T1 or T2 ticket closes its full decoder. A cancel of running work makes the task close the
    /// decoder at its end.
    /// </remarks>
    public void Cancel(ResidencyTicket ticket)
    {
        if (!ticket.IsValid)
            return;

        IStreamingAudioDecoder? decoder;
        lock (_sync)
        {
            if (!_tickets.Remove(ticket.Id, out BackendTicket? entry))
                return;

            entry.Cancelled = true;
            decoder = entry.Decoder;
            entry.Decoder = null;
        }
        decoder?.Dispose();
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

    /// <summary>Cancels all tickets and closes their decoders. A running task closes its decoder at its end.</summary>
    public void Dispose()
    {
        List<IStreamingAudioDecoder>? decoders = null;
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _planner = null;
            foreach (BackendTicket ticket in _tickets.Values)
            {
                ticket.Cancelled = true;
                if (ticket.Decoder is null)
                    continue;
                (decoders ??= []).Add(ticket.Decoder);
                ticket.Decoder = null;
            }
            _tickets.Clear();
        }

        if (decoders is null)
            return;
        for (int i = 0; i < decoders.Count; i++)
            decoders[i].Dispose();
    }

    private void Load(BackendTicket ticket)
    {
        lock (_sync)
        {
            if (ticket.Cancelled)
                return;
        }

        IStreamingAudioDecoder? decoder = null;
        Exception? failure = null;
        try
        {
            decoder = _openDecoder(ticket.Source.FullPath);
            decoder.SeekToFrame(ticket.Source.GetFileTime(ticket.Deadline).ToSamples(decoder.SampleRate));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex;
        }

        CutsceneResidencyPlanner? planner = null;
        bool keep = false;
        lock (_sync)
        {
            if (failure is null && !ticket.Cancelled)
            {
                // The level is set before the planner hears of the completion.
                ticket.Decoder = decoder;
                ticket.Complete = true;
                planner = _planner;
                keep = true;
            }
        }

        if (failure is not null)
        {
            Interlocked.Increment(ref _failedLoadCount);
            Debug.LogWarning(ELogCategory.Audio, "Cutscene audio residency: the full decoder of '{0}' did not open and seek at level T{1}: {2}", ticket.Source.Reference, ticket.Level, failure.Message);
        }

        if (!keep)
        {
            decoder?.Dispose();
            return;
        }
        planner?.NotifyCompleted(new ResidencyTicket(ticket.Id));
    }

    private ResidencyTicket Reject(in ResidencyRequest request, string reason)
    {
        Interlocked.Increment(ref _rejectedRequestCount);
        Debug.LogWarning(ELogCategory.Audio, "Cutscene audio residency: the request for asset {0} at level T{1} (generation {2}) was not accepted: {3}.", request.AssetId, request.TargetLevel, request.Generation, reason);
        return ResidencyTicket.None;
    }

    /// <summary>
    /// Gets the inputs of the backend of an asset as one string: the folder of the asset file, the duration, each audio clip
    /// (source path, start, duration, offset, and the length and last write time of its source file), and each proxy
    /// (reference, source hash, data length). The file state makes an edit of a source file on disk change the string,
    /// because it can change whether a proxy is current.
    /// Two compiles with the same string give the same backend.
    /// </summary>
    /// <param name="asset">The cutscene asset.</param>
    internal static string GetInputSignature(CutsceneAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        StringBuilder text = new();
        string? assetDirectory = Path.GetDirectoryName(asset.FilePath);
        text.Append(assetDirectory).Append('|').Append(asset.Duration.Flicks);
        for (int t = 0; t < asset.Tracks.Count; t++)
        {
            if (asset.Tracks[t] is not AudioTrack track)
                continue;
            for (int c = 0; c < track.Clips.Count; c++)
            {
                if (track.Clips[c] is not AudioTrackClip clip)
                    continue;
                text.Append("|c:").Append(t).Append(',').Append(clip.SourcePath).Append(',').Append(clip.Start.Flicks)
                    .Append(',').Append(clip.Duration.Flicks).Append(',').Append(clip.ClipOffset.Flicks);
                AppendFileState(text, clip.SourcePath, assetDirectory);
            }
        }

        List<AudioProxyAsset> proxies = asset.AudioProxies;
        if (proxies is not null)
            for (int i = 0; i < proxies.Count; i++)
                if (proxies[i] is { } proxy)
                    text.Append("|p:").Append(proxy.SourceReference).Append(',').Append(proxy.SourceContentHash).Append(',').Append(proxy.EncodedData.Length);
        return text.ToString();
    }

    // Appends the length and the last write time of a source file, or a marker for a file that does not exist or has no path.
    private static void AppendFileState(StringBuilder text, string? sourcePath, string? assetDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return;

        try
        {
            FileInfo info = new(AudioTrackEvaluator.ResolveSourcePath(sourcePath, assetDirectory));
            if (info.Exists)
                text.Append(',').Append(info.Length).Append(',').Append(info.LastWriteTimeUtc.Ticks);
            else
                text.Append(",missing");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            text.Append(",unreadable");
        }
    }

    private void CollectSources(CutsceneAsset asset)
    {
        List<AudioProxyAsset> proxies = asset.AudioProxies;
        if (proxies is null || proxies.Count == 0)
            return;

        string? assetDirectory = Path.GetDirectoryName(asset.FilePath);
        for (int t = 0; t < asset.Tracks.Count; t++)
        {
            if (asset.Tracks[t] is not AudioTrack track)
                continue;

            for (int c = 0; c < track.Clips.Count; c++)
            {
                if (track.Clips[c] is not AudioTrackClip clip || string.IsNullOrWhiteSpace(clip.SourcePath))
                    continue;

                string fullPath = AudioTrackEvaluator.ResolveSourcePath(clip.SourcePath, assetDirectory);
                if (!CutsceneAudioProxySources.TryGetSourceReference(clip.SourcePath, fullPath, out string? reference))
                    continue;
                if (CutsceneAudioProxySources.FindProxy(proxies, reference) is not { } proxy)
                    continue;

                Guid assetId = CutsceneResidencyAssetKeys.ForAudioSource(proxy.SourceReference);
                if (!_sources.TryGetValue(assetId, out AudioSourceEntry? source))
                {
                    // The track evaluator compares the same hash and reports a stale proxy, so the backend does not report it again.
                    bool current = CutsceneAudioProxySources.IsCurrent(proxy, fullPath, out _);
                    source = new AudioSourceEntry(assetId, proxy.SourceReference, fullPath, proxy, current);
                    _sources.Add(assetId, source);
                    _sourceOrder.Add(source);
                }
                source.Clips.Add(new AudioClipRange(clip.Start, clip.Duration, clip.ClipOffset));
            }
        }
    }

    private void AddBucket(CutsceneResidencyManifestBuilder builder, long start, long end, long duration)
    {
        builder.BeginBucket(new CutsceneTime(start), new CutsceneTime(end), startsAtCut: false);
        for (int s = 0; s < _sourceOrder.Count; s++)
        {
            List<AudioClipRange> clips = _sourceOrder[s].Clips;
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

    /// <summary>One audio clip of a source: its range on the timeline and its offset in the file.</summary>
    private readonly record struct AudioClipRange(CutsceneTime Start, CutsceneTime Duration, CutsceneTime ClipOffset)
    {
        public (long Start, long End) GetRange(long timelineDuration)
        {
            long start = Math.Clamp(Start.Flicks, 0L, timelineDuration);
            long end = Math.Clamp((Start + Duration).Flicks, 0L, timelineDuration);
            return (start, end);
        }
    }

    /// <summary>One source file with a proxy, and the clips that play it.</summary>
    private sealed class AudioSourceEntry(Guid assetId, string reference, string fullPath, AudioProxyAsset proxy, bool proxyIsCurrent)
    {
        public bool ProxyIsCurrent { get; } = proxyIsCurrent;
        public Guid AssetId { get; } = assetId;
        public string Reference { get; } = reference;
        public string FullPath { get; } = fullPath;
        public AudioProxyAsset Proxy { get; } = proxy;
        public List<AudioClipRange> Clips { get; } = [];

        // The file time of the clip that plays at the deadline. Before a clip, the start of the next clip. After all clips, the end of the last clip.
        public CutsceneTime GetFileTime(CutsceneTime deadline)
        {
            CutsceneTime? next = null;
            CutsceneTime nextStart = default;
            CutsceneTime last = default;
            CutsceneTime lastEnd = default;
            for (int i = 0; i < Clips.Count; i++)
            {
                AudioClipRange clip = Clips[i];
                CutsceneTime end = clip.Start + clip.Duration;
                if (deadline >= clip.Start && deadline < end)
                    return clip.ClipOffset + (deadline - clip.Start);
                if (clip.Start > deadline && (next is null || clip.Start < nextStart))
                {
                    next = clip.ClipOffset;
                    nextStart = clip.Start;
                }
                if (i == 0 || end > lastEnd)
                {
                    lastEnd = end;
                    last = clip.ClipOffset + clip.Duration;
                }
            }
            return next ?? last;
        }
    }

    /// <summary>One request of the planner. A complete T1 or T2 ticket holds the open full decoder.</summary>
    private sealed class BackendTicket(AudioResidencyBackend owner, long id, AudioSourceEntry source, int level, CutsceneTime deadline)
    {
        public AudioResidencyBackend Owner { get; } = owner;
        public long Id { get; } = id;
        public AudioSourceEntry Source { get; } = source;
        public int Level { get; } = level;
        public CutsceneTime Deadline { get; } = deadline;
        public bool Complete { get; set; }
        public bool Cancelled { get; set; }
        public IStreamingAudioDecoder? Decoder { get; set; }
    }
}
