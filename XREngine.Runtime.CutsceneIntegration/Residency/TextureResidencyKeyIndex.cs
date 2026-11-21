using System.Runtime.CompilerServices;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Cutscenes;

/// <summary>
/// Finds the live textures of a stable key (<see cref="CutsceneResidencyAssetKeys.ForTexture(XRTexture2D)"/>). It indexes the
/// textures of the object cache (<see cref="XRObjectBase.ObjectsCache"/>).
/// </summary>
/// <remarks>
/// <para>
/// A key can have more than one live texture, for example the eager and the deferred texture of one import. The index
/// gives all of them, in the order of their runtime IDs. That order is the same within one build. It can change at the
/// next start, because an import makes new runtime IDs, but the backend sends each request to every texture of the key.
/// </para>
/// <para>
/// The index builds at its first miss. After <see cref="AllowRebuild"/>, the next <see cref="Find"/> builds again if the
/// key misses, if its textures are dead, or if the object count changed since the last build (so a new duplicate of a key
/// that hits is found). The residency session allows it one time for each manifest selection, so a selection scans the
/// object cache at most one time, also when many keys miss. The count check is one read, and no frame path calls it.
/// The index keeps the key of each texture that it saw, so a new build hashes only new textures. A build allocates, so
/// it is for an open or a selection, not for each frame. All members are safe on any thread.
/// </para>
/// </remarks>
internal sealed class TextureResidencyKeyIndex
{
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, List<WeakReference<XRTexture2D>>> _textures = [];
    private readonly ConditionalWeakTable<XRTexture2D, StrongBox<Guid>> _keys = new();
    private readonly List<XRTexture2D> _buildScratch = [];
    private bool _rebuildAllowed = true;
    private int _builtObjectCount = -1;

    /// <summary>The number of times that the index was built. Tests read it.</summary>
    public int BuildCount { get; private set; }

    /// <summary>Lets the next miss build the index again. Call it one time for each manifest selection.</summary>
    public void AllowRebuild()
    {
        lock (_sync)
            _rebuildAllowed = true;
    }

    /// <summary>
    /// Makes the next <see cref="Find"/> build the index again, also on a hit with no change of the object count. A caller
    /// that found a dead texture uses it, because a destroy and a new texture leave the count the same.
    /// </summary>
    public void RequireRebuild()
    {
        lock (_sync)
        {
            _rebuildAllowed = true;
            _builtObjectCount = -1;
        }
    }

    /// <summary>Adds the live textures of a key to a list, in the order of their runtime IDs.</summary>
    /// <param name="key">The stable key.</param>
    /// <param name="destination">The list that receives the textures. The method does not clear it.</param>
    /// <returns>The number of textures that the method added.</returns>
    public int Find(Guid key, List<XRTexture2D> destination)
    {
        lock (_sync)
        {
            int start = destination.Count;
            int added = AddLive(key, destination);
            if (!_rebuildAllowed || (added > 0 && XRObjectBase.ObjectsCache.Count == _builtObjectCount))
                return added;

            destination.RemoveRange(start, added);
            Build();
            return AddLive(key, destination);
        }
    }

    private int AddLive(Guid key, List<XRTexture2D> destination)
    {
        if (!_textures.TryGetValue(key, out List<WeakReference<XRTexture2D>>? entries))
            return 0;

        int added = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].TryGetTarget(out XRTexture2D? texture) && IsLive(texture))
            {
                destination.Add(texture);
                added++;
            }
        }
        return added;
    }

    private void Build()
    {
        _rebuildAllowed = false;
        BuildCount++;
        _textures.Clear();
        _builtObjectCount = XRObjectBase.ObjectsCache.Count;

        List<XRTexture2D> live = _buildScratch;
        live.Clear();
        foreach (KeyValuePair<Guid, XRObjectBase> pair in XRObjectBase.ObjectsCache)
            if (pair.Value is XRTexture2D texture && IsLive(texture))
                live.Add(texture);

        // The runtime ID order makes the order of the textures of one key the same at each build.
        live.Sort(static (left, right) => left.ID.CompareTo(right.ID));
        for (int i = 0; i < live.Count; i++)
        {
            XRTexture2D texture = live[i];
            Guid key = _keys.GetValue(texture, static t => new StrongBox<Guid>(CutsceneResidencyAssetKeys.ForTexture(t))).Value;
            if (key == Guid.Empty)
                continue;

            if (!_textures.TryGetValue(key, out List<WeakReference<XRTexture2D>>? entries))
            {
                entries = new List<WeakReference<XRTexture2D>>(1);
                _textures.Add(key, entries);
            }
            entries.Add(new WeakReference<XRTexture2D>(texture));
        }
        live.Clear();
    }

    /// <summary>True if a texture is not destroyed and not queued for destroy.</summary>
    internal static bool IsLive(XRTexture2D texture)
        => !texture.IsDestroyed && !texture.IsDestroyQueued;
}
