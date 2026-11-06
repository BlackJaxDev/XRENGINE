using System.Runtime.CompilerServices;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Rendering;

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
/// By default, the backend holds the <see cref="ETextureStreamingBudgetProfile.Cutscene"/> budget profile while it
/// exists. The profile is global to the streaming manager, so the backends of one manager share it with a reference count:
/// the first backend selects the cutscene profile, and the last <see cref="Dispose"/> selects
/// <see cref="ETextureStreamingBudgetProfile.Default"/> again.
/// </para>
/// </remarks>
public sealed class TextureResidencyBackend : IResidencyBackend, IDisposable
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

    private static readonly Lock ProfileOwnersSync = new();
    private static readonly ConditionalWeakTable<ImportedTextureStreamingManager, StrongBox<int>> ProfileOwners = new();

    private readonly ImportedTextureStreamingManager _manager;
    private readonly Func<Guid, XRTexture2D?> _resolveTexture;
    private readonly Action<TextureResidencyTicket> _onRequestCompleted;
    private readonly bool _ownsBudgetProfile;
    private readonly Lock _sync = new();
    private readonly Dictionary<long, Guid> _ticketAssets = [];
    private readonly Dictionary<Guid, List<TicketLevel>> _assetTickets = [];

    private CutsceneResidencyPlanner? _planner;
    private bool _disposed;
    private bool _hasPlayhead;
    private long _playheadFlicks;
    private double _rate = 1.0;
    private int _rejectedRequestCount;

    /// <summary>
    /// Creates a backend that finds textures in the loaded assets of an asset manager and in the object cache, by runtime ID
    /// or by stable key.
    /// </summary>
    /// <param name="assets">The asset manager that holds the loaded textures.</param>
    /// <param name="useCutsceneBudgetProfile">True to hold the cutscene budget profile until <see cref="Dispose"/>.</param>
    public TextureResidencyBackend(AssetManager assets, bool useCutsceneBudgetProfile = true)
        : this(ImportedTextureStreamingManager.Instance, CreateAssetResolver(assets), useCutsceneBudgetProfile)
    {
    }

    /// <summary>Creates a backend that finds textures with a callback.</summary>
    /// <param name="resolveTexture">Returns the loaded texture of an asset, or null if the asset is not a loaded texture.</param>
    /// <param name="useCutsceneBudgetProfile">True to hold the cutscene budget profile until <see cref="Dispose"/>.</param>
    public TextureResidencyBackend(Func<Guid, XRTexture2D?> resolveTexture, bool useCutsceneBudgetProfile = true)
        : this(ImportedTextureStreamingManager.Instance, resolveTexture, useCutsceneBudgetProfile)
    {
    }

    internal TextureResidencyBackend(
        ImportedTextureStreamingManager manager,
        Func<Guid, XRTexture2D?> resolveTexture,
        bool useCutsceneBudgetProfile)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(resolveTexture);

        _manager = manager;
        _resolveTexture = resolveTexture;
        _onRequestCompleted = OnRequestCompleted;
        _manager.ResidencyRequestCompleted += _onRequestCompleted;
        _ownsBudgetProfile = useCutsceneBudgetProfile;
        if (_ownsBudgetProfile)
            AcquireBudgetProfile(_manager);
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

        XRTexture2D? texture = _resolveTexture(request.AssetId);
        if (texture is null)
            return Reject(request, "the asset is not a loaded texture");

        if (!TryResolveTargetMip(texture, request.TargetLevel, out int targetMip))
            return Reject(request, "the source size of the texture is not known yet");

        long deadlineFrame = ResolveDeadlineFrame(request.Deadline);
        JobPriority priority = ResolvePriority(request);

        // Hold the lock across the manager call, so that a completion that the manager reports
        // at once on another thread waits until the ticket is in the map.
        lock (_sync)
        {
            // Dispose can run between the first check and this lock. A request after it must not
            // register, because nothing would cancel it.
            if (_disposed)
                return ResidencyTicket.None;

            TextureResidencyTicket ticket = _manager.RequestResidency(texture, targetMip, deadlineFrame, priority, request.Generation);
            if (!ticket.IsValid)
                return Reject(request, "texture streaming does not track the texture");

            _ticketAssets.Add(ticket.Id, request.AssetId);
            if (!_assetTickets.TryGetValue(request.AssetId, out List<TicketLevel>? tickets))
            {
                tickets = new List<TicketLevel>(3);
                _assetTickets.Add(request.AssetId, tickets);
            }
            tickets.Add(new TicketLevel(ticket.Id, request.TargetLevel));
            return new ResidencyTicket(ticket.Id);
        }
    }

    /// <inheritdoc />
    /// <remarks>On a complete ticket, this releases the hold of the requested mip. The texture goes back to visibility-driven residency.</remarks>
    public void Cancel(ResidencyTicket ticket)
    {
        if (!ticket.IsValid)
            return;

        lock (_sync)
        {
            if (!_ticketAssets.Remove(ticket.Id, out Guid assetId))
                return;

            RemoveAssetTicket(assetId, ticket.Id);
        }

        _manager.CancelResidency(new TextureResidencyTicket(ticket.Id));
    }

    /// <inheritdoc />
    /// <remarks>True if the asset is a live texture now. It does not check that streaming tracks the texture.</remarks>
    public bool CanResolve(Guid assetId)
        => !_disposed && _resolveTexture(assetId) is not null;

    /// <inheritdoc />
    /// <remarks>The level is the highest level of a ticket of this backend whose mip is resident now.</remarks>
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
        if (!_assetTickets.TryGetValue(assetId, out List<TicketLevel>? tickets))
            return EResidencyLevel.None;

        int level = (int)EResidencyLevel.None;
        for (int i = 0; i < tickets.Count; i++)
        {
            TicketLevel entry = tickets[i];
            if (entry.Level > level
                && _manager.GetResidencyRequestState(new TextureResidencyTicket(entry.TicketId)) == ETextureResidencyRequestState.Resident)
            {
                level = entry.Level;
            }
        }

        return (EResidencyLevel)level;
    }

    /// <summary>
    /// Cancels all tickets of this backend, stops the completion reports, and releases the hold on the cutscene
    /// budget profile. The last holder of the manager selects the default profile.
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
            foreach (long ticketId in _ticketAssets.Keys)
                _manager.CancelResidency(new TextureResidencyTicket(ticketId));
            _ticketAssets.Clear();
            _assetTickets.Clear();
        }

        if (_ownsBudgetProfile)
            ReleaseBudgetProfile(_manager);
    }

    /// <summary>The number of backends that hold the cutscene budget profile of a manager.</summary>
    /// <param name="manager">The streaming manager.</param>
    internal static int GetBudgetProfileOwnerCount(ImportedTextureStreamingManager manager)
    {
        lock (ProfileOwnersSync)
            return ProfileOwners.TryGetValue(manager, out StrongBox<int>? owners) ? owners.Value : 0;
    }

    private static void AcquireBudgetProfile(ImportedTextureStreamingManager manager)
    {
        lock (ProfileOwnersSync)
        {
            StrongBox<int> owners = ProfileOwners.GetValue(manager, static _ => new StrongBox<int>());
            if (owners.Value++ == 0)
                manager.SetBudgetProfile(ETextureStreamingBudgetProfile.Cutscene);
        }
    }

    private static void ReleaseBudgetProfile(ImportedTextureStreamingManager manager)
    {
        lock (ProfileOwnersSync)
        {
            if (!ProfileOwners.TryGetValue(manager, out StrongBox<int>? owners) || owners.Value <= 0)
                return;

            if (--owners.Value == 0)
                manager.SetBudgetProfile(ETextureStreamingBudgetProfile.Default);
        }
    }

    private void OnRequestCompleted(TextureResidencyTicket ticket)
    {
        CutsceneResidencyPlanner? planner;
        lock (_sync)
        {
            if (_disposed || !_ticketAssets.ContainsKey(ticket.Id))
                return;

            planner = _planner;
        }

        // The manager sets the request state before it raises the event, so GetLevel already reports the level.
        planner?.NotifyCompleted(new ResidencyTicket(ticket.Id));
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
        if (!_assetTickets.TryGetValue(assetId, out List<TicketLevel>? tickets))
            return;

        for (int i = 0; i < tickets.Count; i++)
        {
            if (tickets[i].TicketId != ticketId)
                continue;

            tickets.RemoveAt(i);
            break;
        }

        if (tickets.Count == 0)
            _assetTickets.Remove(assetId);
    }

    private static Func<Guid, XRTexture2D?> CreateAssetResolver(AssetManager assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        TextureResidencyKeyIndex keys = new();
        return assetId => ResolveLoadedTexture(assets, keys, assetId);
    }

    /// <summary>
    /// Finds a loaded texture by the ID that a manifest records for it. First it looks for a runtime ID in the loaded assets
    /// of the asset manager and then in the object cache, because a texture that a model import creates is not an asset of
    /// the manager. Then it looks for a stable key of <see cref="CutsceneResidencyAssetKeys"/>, because a model import makes
    /// new runtime IDs at each import.
    /// </summary>
    /// <param name="assets">The asset manager that holds the loaded texture assets.</param>
    /// <param name="keys">The index of the stable texture keys.</param>
    /// <param name="assetId">The ID that the manifest records for the texture.</param>
    /// <returns>The texture, or null if the ID is not a live texture.</returns>
    internal static XRTexture2D? ResolveLoadedTexture(AssetManager assets, TextureResidencyKeyIndex keys, Guid assetId)
    {
        if (assets.TryGetAssetByID(assetId, out XRAsset? asset))
            return asset as XRTexture2D;

        if (XRObjectBase.ObjectsCache.TryGetValue(assetId, out XRObjectBase? cached) && cached is XRTexture2D { IsDestroyed: false } texture)
            return texture;

        return keys.Find(assetId);
    }

    private readonly record struct TicketLevel(long TicketId, int Level);
}
