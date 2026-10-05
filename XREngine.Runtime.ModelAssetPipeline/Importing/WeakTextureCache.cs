using System;
using System.Collections.Generic;
using XREngine.Rendering;

namespace XREngine
{
    /// <summary>
    /// Shares imported file-backed textures by (path, sampler) while something
    /// references them, without keeping them alive itself. Entries whose texture was
    /// collected are replaced on the next request and pruned as the cache grows.
    /// Import-time only; not used on any per-frame path.
    /// </summary>
    internal sealed class WeakTextureCache
    {
        private const int PruneInterval = 256;

        private readonly Dictionary<(string path, string samplerName), WeakReference<XRTexture2D>> _entries = [];
        private readonly object _sync = new();
        private int _insertionsSincePrune;

        /// <summary>
        /// Returns the live texture for <paramref name="key"/>, or creates one with
        /// <paramref name="factory"/> and caches it weakly. Creation runs under the
        /// cache lock so concurrent importers never build duplicates.
        /// </summary>
        public XRTexture2D GetOrAdd<TState>(
            (string path, string samplerName) key,
            TState state,
            Func<(string path, string samplerName), TState, XRTexture2D> factory)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out WeakReference<XRTexture2D>? reference) &&
                    reference.TryGetTarget(out XRTexture2D? existing))
                {
                    return existing;
                }

                XRTexture2D created = factory(key, state);
                if (reference is null)
                    _entries.Add(key, new WeakReference<XRTexture2D>(created));
                else
                    reference.SetTarget(created);

                if (++_insertionsSincePrune >= PruneInterval)
                    PruneNoLock();
                return created;
            }
        }

        private void PruneNoLock()
        {
            _insertionsSincePrune = 0;
            List<(string path, string samplerName)>? dead = null;
            foreach (KeyValuePair<(string path, string samplerName), WeakReference<XRTexture2D>> entry in _entries)
            {
                if (!entry.Value.TryGetTarget(out _))
                    (dead ??= []).Add(entry.Key);
            }

            if (dead is null)
                return;

            foreach ((string path, string samplerName) key in dead)
                _entries.Remove(key);
        }
    }
}
