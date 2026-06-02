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

    internal static readonly ConcurrentDictionary<Type, List<GenericRenderObject>> _roCache = [];

    public static IReadOnlyDictionary<Type, List<GenericRenderObject>> RenderObjectCache => _roCache;

    private static readonly AsyncLocal<int> ApiWrapperCreationSuppressionDepth = new();

    public static IDisposable EnterApiWrapperCreationSuppressionScope()
        => new ApiWrapperCreationSuppressionScope();

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
    {
        lock (RenderObjectCache)
        {
            var type = GetType();
            if (!_roCache.TryGetValue(type, out var list))
                _roCache.TryAdd(type, list = []);
            list.Add(this);
        }

        if (ApiWrapperCreationSuppressionDepth.Value == 0)
            GetWrappers();
    }

    private void GetWrappers()
    {
        AbstractRenderAPIObject?[]? wrappers = RuntimeRenderObjectServices.Current?.CreateObjectsForAllOwners(this);
        if (wrappers is null || wrappers.Length == 0)
            return;

        lock (_apiWrappers)
        {
            foreach (var wrapper in wrappers)
                if (wrapper is not null)
                    _apiWrappers.Add(wrapper);
        }
    }

    ~GenericRenderObject()
    {
        Destroy();

        lock (RenderObjectCache)
        {
            if (_roCache.TryGetValue(GetType(), out var list))
                list.Remove(this);
        }
    }

    public string GetDescribingName()
    {
        string name = $"{GetType().Name} {GetCacheIndex()}";
        if (!string.IsNullOrWhiteSpace(Name))
            name += $" '{Name}'";
        return name;
    }

    protected override void OnDestroying()
    {
        base.OnDestroying();

        AbstractRenderAPIObject[] wrappersSnapshot;
        lock (_apiWrappers)
            wrappersSnapshot = [.. _apiWrappers];

        foreach (var wrapper in wrappersSnapshot)
        {
            try { wrapper.Owner.RemoveAPIRenderObject(this); } catch { }
            try { wrapper.Destroy(); } catch { }
        }

        // Drop ourselves from the global render-object cache so diagnostics and panels stop
        // reporting this object the instant it is destroyed, instead of waiting for finalization.
        lock (RenderObjectCache)
        {
            if (_roCache.TryGetValue(GetType(), out var list))
                list.Remove(this);
        }
    }

    public void AddWrapper(AbstractRenderAPIObject apiRO)
    {
        lock (_apiWrappers)
        {
            if (_apiWrappers.Contains(apiRO))
                return;

            _apiWrappers.Add(apiRO);
        }
    }

    public void RemoveWrapper(AbstractRenderAPIObject apiRO)
    {
        lock (_apiWrappers)
        {
            if (_apiWrappers.TryRemove(apiRO))
                return;

            RuntimeRenderObjectServices.Current?.LogWarning(
                $"Failed to remove API wrapper for {GetDescribingName()} from owner '{apiRO.Owner.RenderApiWrapperOwnerName}'.");
        }
    }
}
