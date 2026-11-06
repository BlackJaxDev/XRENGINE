using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Cutscenes;

/// <summary>
/// Finds a live texture by its stable key (<see cref="CutsceneResidencyAssetKeys.ForTexture"/>). It indexes the textures of
/// the object cache (<see cref="XRObjectBase.ObjectsCache"/>) when it is first used.
/// </summary>
/// <remarks>
/// A miss builds the index again only if the object count changed since the last build, or if the indexed texture of the
/// key was destroyed. So a key that does not resolve costs at most one scan until the objects change. A build scans all
/// live objects and allocates, so it is for an open or a plan, not for each frame. All members are safe on any thread.
/// </remarks>
internal sealed class TextureResidencyKeyIndex
{
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, WeakReference<XRTexture2D>> _textures = [];
    private int _indexedObjectCount = -1;

    /// <summary>The number of times that the index was built. Tests read it.</summary>
    public int BuildCount { get; private set; }

    /// <summary>Finds the live texture of a key.</summary>
    /// <param name="key">The stable key.</param>
    /// <returns>The texture, or null if no live texture has the key.</returns>
    public XRTexture2D? Find(Guid key)
    {
        lock (_sync)
        {
            bool stale = false;
            if (_textures.TryGetValue(key, out WeakReference<XRTexture2D>? entry))
            {
                if (entry.TryGetTarget(out XRTexture2D? texture) && !texture.IsDestroyed)
                    return texture;
                stale = true;
            }

            int objectCount = XRObjectBase.ObjectsCache.Count;
            if (!stale && objectCount == _indexedObjectCount)
                return null;

            Build();
            _indexedObjectCount = objectCount;
            return _textures.TryGetValue(key, out entry) && entry.TryGetTarget(out XRTexture2D? found) && !found.IsDestroyed
                ? found
                : null;
        }
    }

    private void Build()
    {
        _textures.Clear();
        BuildCount++;
        foreach (KeyValuePair<Guid, XRObjectBase> pair in XRObjectBase.ObjectsCache)
        {
            if (pair.Value is not XRTexture2D { IsDestroyed: false } texture)
                continue;

            Guid key = CutsceneResidencyAssetKeys.ForTexture(texture);

            // The first live texture of a source file keeps the key. Only that texture gets the requests of the key.
            if (key != Guid.Empty)
                _textures.TryAdd(key, new WeakReference<XRTexture2D>(texture));
        }
    }
}
