using MemoryPack;
using System.Collections.Concurrent;
using System.ComponentModel;
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
        if (!now)
            return;

        lock (_apiWrappers)
        {
            foreach (var wrapper in APIWrappers)
                wrapper.Destroy();
        }
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

        lock (_apiWrappers)
        {
            foreach (var wrapper in _apiWrappers)
                wrapper.Destroy();
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
