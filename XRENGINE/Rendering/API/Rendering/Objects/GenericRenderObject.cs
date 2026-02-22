using MemoryPack;
using System.Collections.Concurrent;
using System.ComponentModel;
using XREngine.Core.Files;
using YamlDotNet.Serialization;

namespace XREngine.Rendering
{
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
        /// True if this object is currently in use by any window rendering API.
        /// </summary>
        [YamlIgnore]
        [Browsable(false)]
        public bool InUse => APIWrappers.Count > 0;

        /// <summary>
        /// This is a list of API-specific render objects attached to each active window that represent this object.
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

        //public void VerifyWrappers()
        //{
        //    //Make sure every current window using this object has a wrapper for it
        //    lock (_apiWrappers)
        //    {
        //        foreach (var window in Engine.Windows)
        //        {
        //            var wrapper = _apiWrappers.FirstOrDefault(w => w.Window == window);
        //            if (wrapper is null)
        //            {
        //                wrapper = Engine.Rendering.CreateObjectForWindow(this, window);
        //                if (wrapper is not null)
        //                    _apiWrappers.Add(wrapper);
        //            }
        //        }
        //    }
        //}

        /// <summary>
        /// Tells API objects to generate this object right now instead of waiting for the first access.
        /// </summary>
        public override void Generate()
        {
            base.Generate();

            //GetWrappers();

            lock (_apiWrappers)
            {
                foreach (var wrapper in APIWrappers)
                    wrapper.Generate();
            }
        }
        public override void Destroy(bool now = false)
        {
            base.Destroy(now);
            if (now)
            {
                lock (_apiWrappers)
                {
                    foreach (var wrapper in APIWrappers)
                        wrapper.Destroy();
                }
            }
        }

        public GenericRenderObject()
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
            lock (_apiWrappers)
            {
                //Create this object for all windows (but don't generate it yet)
                var wrappers = Engine.Rendering.CreateObjectsForAllWindows(this);
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
                //Destroy all API wrappers that wrap this object
                //Debug.Out($"{GetDescribingName()} is being destroyed. Destroying {_apiWrappers.Count} API wrapper{(_apiWrappers.Count == 1 ? "" : "s")}.");
                foreach (var wrapper in _apiWrappers)
                    wrapper.Destroy();
            }
        }

        internal void AddWrapper(AbstractRenderAPIObject apiRO)
        {
            lock (_apiWrappers)
            {
                if (_apiWrappers.Contains(apiRO))
                    return;

                _apiWrappers.Add(apiRO);
                //Debug.Out($"Added API wrapper for {GetDescribingName()} to window '{apiRO.Window.Window.Title}'.");
            }
        }
        internal void RemoveWrapper(AbstractRenderAPIObject apiRO)
        {
            lock (_apiWrappers)
            {
                if (!_apiWrappers.TryRemove(apiRO))
                    Debug.LogWarning($"Failed to remove API wrapper for {GetDescribingName()} from window '{apiRO.Window.Window.Title}'.");
            }
        }
    }
}
