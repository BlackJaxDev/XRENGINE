using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Generic OpenGL object base class for specific derived generic render objects.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public abstract class VkObject<T> : VkObjectBase where T : GenericRenderObject
    {
        public Device Device => Renderer.device;
        public PhysicalDevice PhysicalDevice => Renderer._physicalDevice;

        private static readonly List<VkObject<T>?> _objectCache = [];
        public IReadOnlyList<VkObject<T>?> ObjectCache => _objectCache;

        public override string GetDescribingName()
        {
            return $"{GetType()} {_bindingId}";
        }

        public uint CacheObject(VkObject<T> obj)
        {
            lock (_objectCache)
            {
                //Find first null slot
                for (int i = 0; i < _objectCache.Count; i++)
                {
                    if (_objectCache[i] is null)
                    {
                        _objectCache[i] = obj;
                        return (uint)i + 1u;
                    }
                }

                //No null slots, add to end
                _objectCache.Add(obj);
                return (uint)_objectCache.Count;
            }
        }
        public VkObject<T>? GetCachedObject(uint id)
        {
            lock (_objectCache)
            {
                if (id == 0 || id > _objectCache.Count)
                    return null;

                return _objectCache[(int)id - 1];
            }
        }
        public void RemoveCachedObject(uint id)
        {
            lock (_objectCache)
            {
                if (id == 0 || id > _objectCache.Count)
                    return;

                if (_objectCache.Count == id)
                    _objectCache.RemoveAt((int)id - 1);
                else
                    _objectCache[(int)id - 1] = null;
            }
        }

        //We want to set the property instead of the field here just in case subclasses override it.
        //It will never be set to null because the constructor requires a non-null value.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public VkObject(VulkanRenderer renderer, T data) : base(renderer)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
            => Data = data;

        protected override GenericRenderObject Data_Internal => Data;

        private T _data;
        public virtual T Data
        {
            get => _data;
            protected set
            {
                if (value == _data)
                    return;

                if (_data is not null)
                {
                    UnlinkData();
                    _data.RemoveWrapper(this);
                }

                _data = value;

                if (_data is not null)
                {
                    _data.AddWrapper(this);
                    LinkData();
                }
            }
        }

        protected abstract void UnlinkData();
        protected abstract void LinkData();

        protected internal override void PostGenerated()
        {
            base.PostGenerated();

            lock (_cache)
            {
                if (_cache.ContainsKey(BindingId))
                {
                    //Shouldn't happen
                    Debug.VulkanWarning($"Vulkan object with binding id {BindingId} already exists in cache.");
                    _cache[BindingId] = this;
                }
                else
                    _cache.Add(BindingId, this);
            }
        }
        protected internal override void PostDeleted()
        {
            lock (_cache)
                _cache.Remove(BindingId);

            base.PostDeleted();
        }
        
        private static readonly EventDictionary<uint, VkObject<T>> _cache = [];
        public static IReadOnlyEventDictionary<uint, VkObject<T>> Cache => _cache;
    }
}