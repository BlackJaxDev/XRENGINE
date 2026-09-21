using MemoryPack;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading;
using XREngine.Core.Files;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

/// <summary>
/// This is the base class for generic render objects that aren't specific to any rendering api.
/// Rendering APIs wrap this object to provide actual rendering functionality.
/// </summary>
[MemoryPackable(GenerateType.NoGenerate)]
public abstract partial class GenericRenderObject : XRAsset
{
    [MemoryPackIgnore]
    private readonly ConcurrentHashSet<AbstractRenderAPIObject> _apiWrappers = [];
    private AbstractRenderAPIObject[] _apiWrapperSnapshot = [];

    /// <summary>Stable, allocation-free view of existing owners for CPU publication; never creates a wrapper.</summary>
    protected ReadOnlySpan<AbstractRenderAPIObject> ApiWrapperSnapshot => Volatile.Read(ref _apiWrapperSnapshot);

    internal static readonly ConcurrentDictionary<Type, List<GenericRenderObject>> _roCache = [];
    private static long _renderObjectCacheRevision;

    public static IReadOnlyDictionary<Type, List<GenericRenderObject>> RenderObjectCache => _roCache;

    private static readonly AsyncLocal<int> ApiWrapperCreationSuppressionDepth = new();
    [ThreadStatic]
    private static RenderObjectPublicationScope? _currentDeferredPublicationScope;

    private int _publicationState = (int)RenderObjectPublicationState.Constructing;
    private bool _publishWrappersOnOwnerFirstUse;
    private bool _publicationScopeManaged;
    private bool _publicationLifecycleInitialized;
    private readonly Lock _ownerFirstPublicationLock = new();
    private Lock OwnerFirstPublicationLock => _ownerFirstPublicationLock;
    private string? _cachedDescribingName;
    private string? _cachedDescribingObjectName;
    private int _cachedDescribingCacheIndex = int.MinValue;
    private long _cachedDescribingCacheRevision = -1;

    public static IDisposable EnterApiWrapperCreationSuppressionScope()
        => new ApiWrapperCreationSuppressionScope();

    /// <summary>
    /// Starts a synchronous CPU-construction transaction. Objects created inside the
    /// transaction are invisible to caches and render owners until root completion.
    /// </summary>
    public static RenderObjectPublicationScope BeginDeferredPublication()
        => new(CurrentDeferredPublicationScope);

    internal static RenderObjectPublicationScope? CurrentDeferredPublicationScope
    {
        get => _currentDeferredPublicationScope;
        set => _currentDeferredPublicationScope = value;
    }

    /// <summary>
    /// True once this object is fully constructed and may be consumed by an API wrapper.
    /// </summary>
    [MemoryPackIgnore]
    [YamlIgnore]
    [Browsable(false)]
    public bool IsApiWrapperPublicationReady
        => Volatile.Read(ref _publicationState) == (int)RenderObjectPublicationState.Published;

    /// <summary>
    /// True when renderer startup must leave this object unwrapped until an owner explicitly consumes it.
    /// </summary>
    [MemoryPackIgnore]
    [YamlIgnore]
    [Browsable(false)]
    public bool PublishWrappersOnOwnerFirstUse => _publishWrappersOnOwnerFirstUse;

    private sealed class ApiWrapperCreationSuppressionScope : IDisposable
    {
        private bool _disposed;

        public ApiWrapperCreationSuppressionScope()
            => ApiWrapperCreationSuppressionDepth.Value++;

        public void Dispose()
        {
            if (_disposed)
                return;

            ApiWrapperCreationSuppressionDepth.Value = Math.Max(0, ApiWrapperCreationSuppressionDepth.Value - 1);
            _disposed = true;
        }
    }

    /// <summary>
    /// True if this object is currently in use by any rendering host.
    /// </summary>
    [YamlIgnore]
    [Browsable(false)]
    public bool InUse => APIWrappers.Count > 0;

    /// <summary>
    /// This is a list of API-specific render objects attached to each active render host that represent this object.
    /// </summary>
    [MemoryPackIgnore]
    [YamlIgnore]
    [Browsable(false)]
    public IReadOnlyCollection<AbstractRenderAPIObject> APIWrappers => _apiWrappers;

    public int GetCacheIndex()
    {
        lock (RenderObjectCache)
        {
            return _roCache.TryGetValue(GetType(), out var list)
                ? list.IndexOf(this)
                : -1;
        }
    }

    /// <summary>
    /// Tells API objects to generate this object right now instead of waiting for the first access.
    /// </summary>
    public override void Generate()
    {
        base.Generate();

        lock (_apiWrappers)
        {
            foreach (var wrapper in APIWrappers)
                wrapper.Generate();
        }
    }

    public override void Destroy(bool now = false)
    {
        base.Destroy(now);
    }

    protected GenericRenderObject()
        : this(deferObjectCachePublication: false)
    {
    }

    protected GenericRenderObject(bool deferObjectCachePublication)
        : base(deferObjectCachePublication)
    {
        _publicationLifecycleInitialized = true;
        if (CurrentDeferredPublicationScope is { } publicationScope)
        {
            _publicationScopeManaged = true;
            publicationScope.Enlist(this);
            if (deferObjectCachePublication)
                JoinDeferredObjectCachePublication();
            return;
        }

        if (deferObjectCachePublication)
        {
            _publishWrappersOnOwnerFirstUse = true;
            return;
        }

        RegisterInRenderObjectCache();
        Volatile.Write(ref _publicationState, (int)RenderObjectPublicationState.Published);

        if (ApiWrapperCreationSuppressionDepth.Value == 0)
            GetWrappers();
    }

    /// <summary>
    /// Publishes an owner-first render object whose derived constructor deliberately
    /// withheld base cache registration. Ambient construction batches remain root-owned.
    /// </summary>
    protected void CompleteOwnerFirstConstruction()
    {
        if (_publicationScopeManaged || IsApiWrapperPublicationReady)
            return;

        lock (OwnerFirstPublicationLock)
        {
            if (IsApiWrapperPublicationReady)
                return;
            if (IsDestroyed)
                throw new ObjectDisposedException(GetType().FullName);
            if (Volatile.Read(ref _publicationState) != (int)RenderObjectPublicationState.Constructing)
                throw new InvalidOperationException(
                    $"Render object '{GetType().Name}' cannot complete owner-first publication from its current state.");

            using RenderObjectPublicationScope publication = BeginDeferredPublication();
            publication.Enlist(this);
            JoinDeferredObjectCachePublication();
            publication.Complete();
        }
    }

    internal void PrepareDeferredPublication()
    {
        lock (OwnerFirstPublicationLock)
        {
            int state = Volatile.Read(ref _publicationState);
            if (IsDestroyed || state == (int)RenderObjectPublicationState.Destroyed)
                throw new InvalidOperationException($"Destroyed render object '{GetType().Name}' cannot complete publication.");
            if (state is (int)RenderObjectPublicationState.ReadyUnpublished or
                (int)RenderObjectPublicationState.Published)
            {
                return;
            }
            if (state != (int)RenderObjectPublicationState.Constructing)
                throw new InvalidOperationException($"Render object '{GetType().Name}' cannot enter publication from state {state}.");

            _publishWrappersOnOwnerFirstUse = true;
            Volatile.Write(ref _publicationState, (int)RenderObjectPublicationState.ReadyUnpublished);
        }
    }

    internal void FinalizeDeferredPublication()
    {
        lock (OwnerFirstPublicationLock)
        {
            int state = Volatile.Read(ref _publicationState);
            if (IsDestroyed || state == (int)RenderObjectPublicationState.Destroyed)
                throw new InvalidOperationException($"Destroyed render object '{GetType().Name}' cannot complete publication.");
            if (state == (int)RenderObjectPublicationState.Published)
                return;
            if (state != (int)RenderObjectPublicationState.ReadyUnpublished)
                throw new InvalidOperationException($"Render object '{GetType().Name}' is not ready for publication.");

            Volatile.Write(ref _publicationState, (int)RenderObjectPublicationState.Published);
        }
    }

    internal static void PublishDeferredBatch(IReadOnlyList<GenericRenderObject> objects)
    {
        lock (RenderObjectCache)
        {
            for (int index = 0; index < objects.Count; index++)
            {
                GenericRenderObject value = objects[index];
                if (value.IsDestroyed || !value.IsApiWrapperPublicationReady)
                {
                    throw new InvalidOperationException(
                        $"Render-object publication batch contains unavailable member '{value.GetType().Name}'.");
                }

                value.RegisterInRenderObjectCacheUnderLock();
            }
        }
    }

    protected override void PublishObjectCacheRegistrationUnderLock()
    {
        // XRObjectBase publishes ordinary objects while the base constructor is still
        // running. Deferred render objects reach this override only after their full
        // construction lifecycle has initialized and reached Published.
        if (!_publicationLifecycleInitialized)
        {
            base.PublishObjectCacheRegistrationUnderLock();
            return;
        }

        lock (OwnerFirstPublicationLock)
        {
            if (Volatile.Read(ref _publicationState) != (int)RenderObjectPublicationState.Published || IsDestroyed)
                throw new InvalidOperationException(
                    $"Render object '{GetType().Name}' cannot enter the object cache before publication or after destruction.");
            base.PublishObjectCacheRegistrationUnderLock();
        }
    }

    private void RegisterInRenderObjectCache()
    {
        lock (RenderObjectCache)
            RegisterInRenderObjectCacheUnderLock();
    }

    private void RegisterInRenderObjectCacheUnderLock()
    {
        Type type = GetType();
        if (!_roCache.TryGetValue(type, out List<GenericRenderObject>? list))
            _roCache.TryAdd(type, list = []);
        if (list.Contains(this))
            return;

        list.Add(this);
        _renderObjectCacheRevision++;
    }

    private void GetWrappers()
    {
        if (!IsApiWrapperPublicationReady)
            throw new InvalidOperationException($"Render object '{GetType().Name}' cannot publish an API wrapper before construction completes.");

        // Explicit hosts have no window registration. Their synchronous creation
        // scope selects one owner without publishing it to other render threads.
        if (_apiWrapperCreationOwner is { } owner)
        {
            if (owner.GetOrCreateAPIRenderObject(this) is { } wrapper)
                AddWrapper(wrapper);
            return;
        }

        AbstractRenderAPIObject?[]? wrappers = RuntimeRenderObjectServices.Current?.CreateObjectsForAllOwners(this);
        if (wrappers is null || wrappers.Length == 0)
            return;

        foreach (AbstractRenderAPIObject? wrapper in wrappers)
            if (wrapper is not null)
                AddWrapper(wrapper);
    }

    /// <summary>
    /// Completes owner-first publication and resolves API wrappers at the first explicit
    /// backend operation. Wrapper creation is restricted to the owning render thread.
    /// </summary>
    protected AbstractRenderAPIObject EnsureApiWrapperForOwnerFirstUse()
    {
        CompleteOwnerFirstConstruction();
        IRenderApiWrapperOwner owner = _apiWrapperCreationOwner ?? AbstractRenderer.Current ??
            throw new InvalidOperationException(
                $"Render object '{GetType().Name}' requested a backend operation before an active render owner was available.");

        if (TryGetActiveWrapperForOwner(owner, out AbstractRenderAPIObject? existing))
            return existing;

        if (_apiWrapperCreationOwner is null && RuntimeEngine.RenderThreadId != 0 && !RuntimeEngine.IsRenderThread)
        {
            throw new InvalidOperationException(
                $"Render object '{GetType().Name}' requires owner-first wrapper creation on render thread {RuntimeEngine.RenderThreadId}, " +
                $"but thread {Environment.CurrentManagedThreadId} requested its first backend operation.");
        }

        lock (OwnerFirstPublicationLock)
        {
            if (TryGetActiveWrapperForOwner(owner, out existing))
                return existing;

            AbstractRenderAPIObject? wrapper = owner.GetOrCreateAPIRenderObject(this);
            if (wrapper is null || wrapper.IsRetired || !owner.OwnsApiWrapper(wrapper))
            {
                throw new InvalidOperationException(
                    $"Render owner '{owner.RenderApiWrapperOwnerName}' failed to create a live wrapper for '{GetType().Name}'.");
            }

            AddWrapper(wrapper);
            return wrapper;
        }
    }

    /// <summary>
    /// Resolves the live wrapper for the selected owner without creating one.
    /// This is used by optional operations, such as mapped-memory probes, which
    /// must not accidentally consume another renderer owner's wrapper.
    /// </summary>
    protected bool TryGetApiWrapperForCurrentOwner(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AbstractRenderAPIObject? result)
    {
        IRenderApiWrapperOwner? owner = _apiWrapperCreationOwner ?? AbstractRenderer.Current;
        if (owner is null)
        {
            result = null;
            return false;
        }

        return TryGetActiveWrapperForOwner(owner, out result);
    }

    private bool TryGetActiveWrapperForOwner(
        IRenderApiWrapperOwner owner,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AbstractRenderAPIObject? result)
    {
        foreach (AbstractRenderAPIObject wrapper in ApiWrapperSnapshot)
            if (!wrapper.IsRetired && owner.OwnsApiWrapper(wrapper))
            {
                result = wrapper;
                return true;
            }

        result = null;
        return false;
    }

    ~GenericRenderObject()
    {
        Destroy();

        lock (RenderObjectCache)
        {
            if (_roCache.TryGetValue(GetType(), out var list))
            {
                if (list.Remove(this))
                    _renderObjectCacheRevision++;
            }
        }
    }

    public string GetDescribingName()
    {
        long cacheRevision = Volatile.Read(ref _renderObjectCacheRevision);
        string? objectName = string.IsNullOrWhiteSpace(Name) ? null : Name;
        string? cached = _cachedDescribingName;
        if (cached is not null &&
            cacheRevision == _cachedDescribingCacheRevision &&
            string.Equals(objectName, _cachedDescribingObjectName, StringComparison.Ordinal))
        {
            return cached;
        }

        int cacheIndex = GetCacheIndex();
        cached = objectName is null
            ? $"{GetType().Name} {cacheIndex}"
            : $"{GetType().Name} {cacheIndex} '{objectName}'";
        _cachedDescribingCacheIndex = cacheIndex;
        _cachedDescribingCacheRevision = cacheRevision;
        _cachedDescribingObjectName = objectName;
        _cachedDescribingName = cached;
        return cached;
    }

    protected override void OnDestroying()
    {
        AbstractRenderAPIObject[] wrappersSnapshot;
        lock (OwnerFirstPublicationLock)
        {
            lock (_apiWrappers)
            {
                Volatile.Write(ref _publicationState, (int)RenderObjectPublicationState.Destroyed);
                wrappersSnapshot = [.. _apiWrappers];
            }

            // Drop ourselves from the global render-object cache while publication and
            // terminal state changes are serialized by the owner-first gate.
            lock (RenderObjectCache)
            {
                if (_roCache.TryGetValue(GetType(), out var list))
                {
                    if (list.Remove(this))
                        _renderObjectCacheRevision++;
                }
            }
        }

        base.OnDestroying();

        foreach (var wrapper in wrappersSnapshot)
        {
            try { wrapper.Owner.RemoveAPIRenderObject(this); } catch { }
            try { wrapper.Retire(); } catch { }
        }

    }

    public void AddWrapper(AbstractRenderAPIObject apiRO)
    {
        bool recordMeshWait = this is XRDataBuffer { IsMeshOwnedBuffer: true };
        long lockWaitStart = recordMeshWait ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        lock (_apiWrappers)
        {
            if (recordMeshWait)
                XRMeshCpuPreparationTelemetry.RecordWrapperLockWait(
                    System.Diagnostics.Stopwatch.GetTimestamp() - lockWaitStart);
            if (!IsApiWrapperPublicationReady || IsDestroyed)
                throw new InvalidOperationException($"Render object '{GetType().Name}' cannot accept an API wrapper before publication or after destruction.");
            if (_apiWrappers.Contains(apiRO))
                return;

            _apiWrappers.Add(apiRO);
            PublishApiWrapperSnapshot();
        }
    }

    public void RemoveWrapper(AbstractRenderAPIObject apiRO)
    {
        lock (_apiWrappers)
        {
            if (_apiWrappers.TryRemove(apiRO))
            {
                PublishApiWrapperSnapshot();
                return;
            }

            RuntimeRenderObjectServices.Current?.LogWarning(
                $"Failed to remove API wrapper for {GetDescribingName()} from owner '{apiRO.Owner.RenderApiWrapperOwnerName}'.");
        }
    }

    // Called only under the collection gate when an owner is added or removed.
    // Allocating here keeps the frequent CPU writer commits allocation-free.
    private void PublishApiWrapperSnapshot()
    {
        AbstractRenderAPIObject[] snapshot = [.. _apiWrappers];
        Volatile.Write(ref _apiWrapperSnapshot, snapshot);
    }
}
