using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Residency;

namespace XREngine.Cutscenes;

/// <summary>
/// A residency backend for streamed textures. It sends the levels of the cutscene residency planner
/// to the imported texture streaming manager as explicit residency requests.
/// </summary>
/// <remarks>
/// <para>
/// T0 is the preview of the texture, T1 is the first mip with a largest side of at most
/// <see cref="CoarseMaxDimension"/>, and T2 is the full source size. The texture quality setting and the
/// VRAM budget can lower T2. A request is not visibility, and it does not pin the texture.
/// </para>
/// <para>
/// Call <see cref="AttachPlanner"/> before <see cref="CutsceneResidencyPlanner.Open"/>, so that the backend can
/// report complete work. Call <see cref="SetPlayhead"/> before each planner update, so that request deadlines
/// become frame deadlines. The backend works on textures that are already loaded. It does not load assets.
/// An asset ID can be a runtime ID or a stable key of <see cref="CutsceneResidencyAssetKeys"/>.
/// </para>
/// <para>
/// One asset can have more than one live texture, for example the eager and the deferred texture of one source file.
/// A request then goes to each of them, and its level is resident only when it is resident for each of them. A texture
/// that streaming does not accept is left out of the request; <see cref="PartialRejectedTextureCount"/> counts it, and the
/// first one of each asset gives one warning.
/// </para>
/// <para>
/// By default, the backend holds a lease on the <see cref="ETextureStreamingBudgetProfile.Cutscene"/> budget profile
/// (<see cref="ITextureResidencyRequests.AcquireBudgetProfile"/>) while it exists. The seam counts the leases of all
/// holders, so the profile goes back to <see cref="ETextureStreamingBudgetProfile.Default"/> only when the last lease is
/// released. Each request of the backend is an owned <see cref="TextureResidencyHandle"/>.
/// </para>
/// </remarks>
public sealed partial class TextureResidencyBackend : IResidencyBackend, IDisposable
{
    /// <summary>The default largest side of T1, in texels.</summary>
    public const uint DefaultCoarseMaxDimension = 512u;

    /// <summary>
    /// The screen coverage, from 0 to 1, at or above which a T1 or T2 request gets <see cref="JobPriority.High"/>.
    /// A request below it gets <see cref="JobPriority.Normal"/>. T0 always gets <see cref="JobPriority.High"/>.
    /// </summary>
    public const float HighPriorityScreenCoverage = 0.25f;

    /// <summary>A mip so high that the request asks for the preview only.</summary>
    private const int PreviewMip = 31;

    private readonly ITextureResidencyRequests _manager;
    private readonly Action<TextureResidencyTicket> _onRequestCompleted;
    private IDisposable? _budgetProfileLease;
    private readonly Lock _sync = new();
    private readonly Dictionary<long, BackendTicket> _tickets = [];
    private readonly Dictionary<long, long> _managerTickets = [];
    private readonly Dictionary<Guid, List<long>> _assetTickets = [];

    private CutsceneResidencyPlanner? _planner;
    private bool _disposed;
    private bool _hasPlayhead;
    private long _playheadFlicks;
    private double _rate = 1.0;
    private int _rejectedRequestCount;
    private int _partialRejectedTextureCount;
    private readonly HashSet<Guid> _partialRejectionWarned = [];
    private long _nextTicketId;

    /// <summary>
    /// Creates a backend that finds textures in the loaded assets of an asset manager and in the object cache, by runtime ID
    /// or by stable key.
    /// </summary>
    /// <param name="assets">The asset manager that holds the loaded textures.</param>
    /// <param name="useCutsceneBudgetProfile">True to hold the cutscene budget profile until <see cref="Dispose"/>.</param>
    public TextureResidencyBackend(AssetManager assets, bool useCutsceneBudgetProfile = true)
        : this(TextureResidencyRequests.Shared, assets ?? throw new ArgumentNullException(nameof(assets)), new TextureResidencyKeyIndex(), useCutsceneBudgetProfile)
    {
    }

    /// <summary>Creates a backend that finds textures with a callback.</summary>
    /// <param name="resolveTexture">Returns the loaded texture of an asset, or null if the asset is not a loaded texture.</param>
    /// <param name="useCutsceneBudgetProfile">True to hold the cutscene budget profile until <see cref="Dispose"/>.</param>
    public TextureResidencyBackend(Func<Guid, XRTexture2D?> resolveTexture, bool useCutsceneBudgetProfile = true)
        : this(TextureResidencyRequests.Shared, resolveTexture, useCutsceneBudgetProfile)
    {
    }

    internal TextureResidencyBackend(
        ITextureResidencyRequests manager,
        Func<Guid, XRTexture2D?> resolveTexture,
        bool useCutsceneBudgetProfile)
        : this(manager, AdaptResolver(resolveTexture), keys: null, useCutsceneBudgetProfile)
    {
    }

    internal TextureResidencyBackend(
        ITextureResidencyRequests manager,
        AssetManager assets,
        TextureResidencyKeyIndex keys,
        bool useCutsceneBudgetProfile)
        : this(manager, (assetId, destination) => ResolveLoadedTextures(assets, keys, assetId, destination), keys, useCutsceneBudgetProfile)
    {
    }

    private TextureResidencyBackend(
        ITextureResidencyRequests manager,
        Action<Guid, List<XRTexture2D>> resolveTextures,
        TextureResidencyKeyIndex? keys,
        bool useCutsceneBudgetProfile)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(resolveTextures);

        _manager = manager;
        _resolveTextures = resolveTextures;
        _keys = keys;
        _onRequestCompleted = OnRequestCompleted;
        _manager.ResidencyRequestCompleted += _onRequestCompleted;
        if (useCutsceneBudgetProfile)
            _budgetProfileLease = _manager.AcquireBudgetProfile(ETextureStreamingBudgetProfile.Cutscene);
    }

    /// <summary>The largest side of T1, in texels. The default is <see cref="DefaultCoarseMaxDimension"/>.</summary>
    public uint CoarseMaxDimension { get; set; } = DefaultCoarseMaxDimension;

    /// <summary>The frame rate that converts a cutscene deadline to a frame deadline. The default is 60.</summary>
    public double EstimatedFramesPerSecond { get; set; } = 60.0;

    /// <summary>
    /// The number of requests that the backend did not accept: the asset is not a loaded texture,
    /// streaming does not track the texture, or the source size of T1 is not known yet.
    /// </summary>
    public int RejectedRequestCount => Volatile.Read(ref _rejectedRequestCount);

    /// <summary>
    /// The number of textures that streaming did not accept in requests that other textures of the same asset accepted.
    /// The request goes on without them, so the level of the asset can be resident while such a texture is not.
    /// </summary>
    public int PartialRejectedTextureCount => Volatile.Read(ref _partialRejectedTextureCount);

    /// <inheritdoc />
    /// <remarks>
    /// True while the last evaluation of the streaming manager fit at least one texture below its target to the VRAM
    /// budget, held promotions because the streamed textures use more than the budget, or held promotions for Vulkan
    /// allocator pressure. The per-frame transition limits of a budget profile are not memory pressure.
    /// </remarks>
    public bool IsUnderMemoryPressure
        => !_disposed && _manager.IsUnderBudgetPressure;

    /// <inheritdoc />
    /// <remarks>The frame of the last streaming evaluation.</remarks>
    public long MemoryPressureFrameId
        => _manager.BudgetPressureFrameId;

    /// <inheritdoc />
    /// <remarks>True while model imports that register textures with streaming are active.</remarks>
    public bool AreAssetsLoading
        => _manager.AreStreamedTexturesImporting;

    /// <summary>
    /// Sets the planner that receives complete work. Call it one time, before <see cref="CutsceneResidencyPlanner.Open"/>.
    /// </summary>
    /// <param name="planner">The planner that uses this backend.</param>
    public void AttachPlanner(CutsceneResidencyPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        lock (_sync)
            _planner = planner;
    }

    /// <summary>
    /// Sets the playhead that converts request deadlines to frame deadlines.
    /// Call it on the update thread before each planner update.
    /// </summary>
    /// <param name="position">The playhead position.</param>
    /// <param name="rate">The playback rate. Zero (a pause) uses rate 1, as the planner window does.</param>
    public void SetPlayhead(CutsceneTime position, double rate)
    {
        _playheadFlicks = position.Flicks;
        _rate = double.IsFinite(rate) ? rate : 1.0;
        _hasPlayhead = true;
    }

    /// <inheritdoc />
    public ResidencyTicket Request(in ResidencyRequest request)
    {
        if (_disposed)
            return ResidencyTicket.None;

        XRTexture2D[] textures = ResolveCached(request.AssetId);
        if (textures.Length == 0)
            return Reject(request, "the asset is not a loaded texture");

        long deadlineFrame = ResolveDeadlineFrame(request.Deadline);
        JobPriority priority = ResolvePriority(request);

        // Hold the lock across the manager calls, so that a completion that the manager reports
        // at once on another thread waits until the ticket is in the maps.
        lock (_sync)
        {
            // Dispose can run between the first check and this lock. A request after it must not
            // register, because nothing would cancel it.
            if (_disposed)
                return ResidencyTicket.None;

            long id = ++_nextTicketId;
            BackendTicket entry = new(request.AssetId, request.TargetLevel, textures.Length);
            bool sizeUnknown = false;
            int notAccepted = 0;
            for (int i = 0; i < textures.Length; i++)
            {
                XRTexture2D texture = textures[i];
                if (!TextureResidencyKeyIndex.IsLive(texture))
                    continue;
                if (!TryResolveTargetMip(texture, request.TargetLevel, out int targetMip))
                {
                    sizeUnknown = true;
                    notAccepted++;
                    continue;
                }

                TextureResidencyHandle? handle = _manager.RequestResidency(texture, targetMip, deadlineFrame, priority, request.Generation);
                if (handle is null)
                {
                    notAccepted++;
                    continue;
                }

                entry.Handles.Add(handle);
                _managerTickets.Add(handle.Ticket.Id, id);
            }

            if (entry.Handles.Count == 0)
            {
                return Reject(request, sizeUnknown
                    ? "the source size of the texture is not known yet"
                    : "texture streaming does not track the texture");
            }

            if (notAccepted > 0)
            {
                Interlocked.Add(ref _partialRejectedTextureCount, notAccepted);
                if (_partialRejectionWarned.Add(request.AssetId))
                    Debug.TexturesWarning($"[Cutscene] {notAccepted} of {textures.Length} textures of asset {request.AssetId} did not accept the residency request at level T{request.TargetLevel}, because streaming does not track them or their source size is not known yet. The request goes on with the other textures.");
            }

            _tickets.Add(id, entry);
            if (!_assetTickets.TryGetValue(request.AssetId, out List<long>? assetTickets))
            {
                assetTickets = new List<long>(3);
                _assetTickets.Add(request.AssetId, assetTickets);
            }
            assetTickets.Add(id);
            return new ResidencyTicket(id);
        }
    }

    /// <inheritdoc />
    /// <remarks>On a complete ticket, this releases the hold of the requested mip. The texture goes back to visibility-driven residency.</remarks>
    public void Cancel(ResidencyTicket ticket)
    {
        if (!ticket.IsValid)
            return;

        BackendTicket? entry;
        lock (_sync)
        {
            if (!_tickets.Remove(ticket.Id, out entry))
                return;

            for (int i = 0; i < entry.Handles.Count; i++)
                _managerTickets.Remove(entry.Handles[i].Ticket.Id);
            RemoveAssetTicket(entry.AssetId, ticket.Id);
        }

        for (int i = 0; i < entry.Handles.Count; i++)
            entry.Handles[i].Dispose();
    }

    /// <inheritdoc />
    /// <remarks>
    /// The level is the highest level of a ticket of this backend whose mip is resident now for each texture of the ticket.
    /// </remarks>
    public EResidencyLevel GetLevel(Guid assetId)
    {
        lock (_sync)
            return GetLevelLocked(assetId);
    }

    /// <inheritdoc />
    /// <remarks>The backend takes its lock one time for all the assets.</remarks>
    public void GetLevels(ReadOnlySpan<Guid> assetIds, Span<EResidencyLevel> levels)
    {
        if (levels.Length < assetIds.Length)
            throw new ArgumentException("The level span is shorter than the asset span.", nameof(levels));

        lock (_sync)
            for (int i = 0; i < assetIds.Length; i++)
                levels[i] = GetLevelLocked(assetIds[i]);
    }

    private EResidencyLevel GetLevelLocked(Guid assetId)
    {
        if (!_assetTickets.TryGetValue(assetId, out List<long>? assetTickets))
            return EResidencyLevel.None;

        int level = (int)EResidencyLevel.None;
        for (int i = 0; i < assetTickets.Count; i++)
        {
            BackendTicket entry = _tickets[assetTickets[i]];
            if (entry.Level > level && IsResidentLocked(entry))
                level = entry.Level;
        }

        return (EResidencyLevel)level;
    }

    private bool IsResidentLocked(BackendTicket entry)
    {
        for (int i = 0; i < entry.Handles.Count; i++)
            if (entry.Handles[i].State != ETextureResidencyRequestState.Resident)
                return false;
        return true;
    }

    /// <summary>
    /// Disposes all request handles of this backend, stops the completion reports, and releases the lease on the cutscene
    /// budget profile.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _planner = null;
            _manager.ResidencyRequestCompleted -= _onRequestCompleted;

            // The manager raises completions outside its own locks, so a cancel here cannot deadlock with them.
            foreach (BackendTicket entry in _tickets.Values)
                for (int i = 0; i < entry.Handles.Count; i++)
                    entry.Handles[i].Dispose();
            _tickets.Clear();
            _managerTickets.Clear();
            _assetTickets.Clear();
        }

        ClearResolved();
        Interlocked.Exchange(ref _budgetProfileLease, null)?.Dispose();
    }

    private void OnRequestCompleted(TextureResidencyTicket ticket)
    {
        CutsceneResidencyPlanner? planner;
        long id;
        lock (_sync)
        {
            if (_disposed || !_managerTickets.TryGetValue(ticket.Id, out id) || !_tickets.TryGetValue(id, out BackendTicket? entry))
                return;

            // The planner gets one completion, when the mip is resident for each texture of the asset.
            if (entry.Notified || !IsResidentLocked(entry))
                return;

            entry.Notified = true;
            planner = _planner;
        }

        // The manager sets the request state before it raises the event, so GetLevel already reports the level.
        planner?.NotifyCompleted(new ResidencyTicket(id));
    }

    private bool TryResolveTargetMip(XRTexture2D texture, int level, out int targetMip)
    {
        switch (level)
        {
            case (int)EResidencyLevel.Proxy:
                targetMip = PreviewMip;
                return true;
            case (int)EResidencyLevel.Coarse:
                targetMip = 0;
                if (!_manager.TryGetSourceMaxDimension(texture, out uint sourceMaxDimension))
                    return false;

                uint coarse = Math.Max(1u, CoarseMaxDimension);
                while ((sourceMaxDimension >> targetMip) > coarse)
                    targetMip++;
                return true;
            case (int)EResidencyLevel.Target:
                targetMip = 0;
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(level), level, "A texture residency level must be T0, T1, or T2.");
        }
    }

    private long ResolveDeadlineFrame(CutsceneTime deadline)
    {
        long now = _manager.CurrentFrameId;
        if (!_hasPlayhead)
            return long.MaxValue;

        double speed = Math.Abs(_rate);
        if (speed == 0.0)
            speed = 1.0;

        long ahead = deadline.Flicks - _playheadFlicks;
        if (_rate < 0.0)
            ahead = -ahead;
        if (ahead <= 0)
            return now;

        double frames = ahead / (double)CutsceneTime.FlicksPerSecond / speed * Math.Max(1.0, EstimatedFramesPerSecond);
        double latest = long.MaxValue - (double)Math.Max(0L, now);
        return frames >= latest ? long.MaxValue : now + (long)Math.Ceiling(frames);
    }

    private static JobPriority ResolvePriority(in ResidencyRequest request)
    {
        if (request.TargetLevel == (int)EResidencyLevel.Proxy)
            return JobPriority.High;

        return request.ScreenCoverage >= HighPriorityScreenCoverage ? JobPriority.High : JobPriority.Normal;
    }

    private ResidencyTicket Reject(in ResidencyRequest request, string reason)
    {
        Interlocked.Increment(ref _rejectedRequestCount);
        Debug.TexturesWarning($"[Cutscene] Texture residency request for asset {request.AssetId} at level T{request.TargetLevel} (generation {request.Generation}) was not accepted: {reason}.");
        return ResidencyTicket.None;
    }

    private void RemoveAssetTicket(Guid assetId, long ticketId)
    {
        if (!_assetTickets.TryGetValue(assetId, out List<long>? assetTickets))
            return;

        assetTickets.Remove(ticketId);
        if (assetTickets.Count == 0)
            _assetTickets.Remove(assetId);
    }

    /// <summary>One request of the planner, with the owned streaming request handle of each live texture of its asset.</summary>
    private sealed class BackendTicket(Guid assetId, int level, int textureCount)
    {
        public Guid AssetId { get; } = assetId;
        public int Level { get; } = level;
        public List<TextureResidencyHandle> Handles { get; } = new(textureCount);
        public bool Notified { get; set; }
    }
}
