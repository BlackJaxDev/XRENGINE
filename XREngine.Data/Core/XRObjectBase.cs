using System.Collections.Concurrent;
using XREngine.Core;
using YamlDotNet.Serialization;

namespace XREngine.Data.Core
{
    /// <summary>
    /// This base class is for any object that is managed by the engine, has a unique ID, and should be destroyed after use.
    /// </summary>
    [Serializable]
    public abstract class XRObjectBase : XRBase, IDisposable, IPoolable
    {
        public Guid ID { get; internal set; } = Guid.NewGuid();
        
        private static ConcurrentDictionary<Guid, XRObjectBase> ObjectsCacheInternal { get; } = [];
        public static IReadOnlyDictionary<Guid, XRObjectBase> ObjectsCache => ObjectsCacheInternal;

        public static void DestroyAllObjects()
        {
            foreach (var obj in ObjectsCacheInternal.Values)
                obj.Destroy();
            ObjectsCacheInternal.Clear();
        }
        public static void ValidateObjectCache()
        {
            foreach (var obj in ObjectsCacheInternal.Values)
                if (obj.IsDestroyed)
                    ObjectsCacheInternal.Remove(obj.ID, out _);
        }

        public XRObjectBase()
        {
            Generate();
        }
        ~XRObjectBase()
        {
            Destroy();
        }

        public virtual void Generate()
        {
            ID = Guid.NewGuid();
            IsDestroyed = false;

            int tries = 0;
            while (ObjectsCacheInternal.ContainsKey(ID))
            {
                //Collision, update ID and try again
                ID = Guid.NewGuid();
                if (tries++ > 10)
                    throw new Exception("Failed to generate a unique ID for an object."); //Highly unlikely
            }
            ObjectsCacheInternal.TryAdd(ID, this);
        }

        /// <summary>
        /// Event that is called when the object is destroyed.
        /// </summary>
        [YamlIgnore]
        public XREvent<XRObjectBase>? Destroyed;
        /// <summary>
        /// Event that is called when the object has been requested to be destroyed.
        /// All listeners must return true for the object to be destroyed.
        /// </summary>
        [YamlIgnore]
        public XRBoolEvent<XRObjectBase>? Destroying;

        private string? _name;
        [YamlMember(Order = 0)]
        public string? Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        /// <summary>
        /// True if the object has been destroyed and no longer exists in a valid state.
        /// </summary>
        [YamlIgnore]
        public bool IsDestroyed { get; private set; } = false;

        private static readonly ConcurrentQueue<XRObjectBase> _objectsToDestroy = new();

        /// <summary>
        /// Processes all objects that have been requested to be destroyed.
        /// Must be called by the engine repeatedly at some interval.
        /// </summary>
        public static void ProcessPendingDestructions()
        {
            while (_objectsToDestroy.TryDequeue(out XRObjectBase? obj))
                obj.Destroy(true);
        }

        public virtual void Destroy(bool now = false)
        {
            if (IsDestroyed)
                return;

            if (!now)
            {
                _objectsToDestroy.Enqueue(this);
                return;
            }

            if (!(Destroying?.Invoke(this) ?? true))
                return;

            OnDestroying();
            ObjectsCacheInternal.Remove(ID, out _);
            IsDestroyed = true;

            Destroyed?.Invoke(this);
        }

        /// <summary>
        /// Called when the object is being destroyed.
        /// </summary>
        protected virtual void OnDestroying() { }

        void IDisposable.Dispose()
        {
            if (IsDestroyed)
                return;

            Destroy();
            GC.SuppressFinalize(this);
        }

        public virtual void OnPoolableReset()
        {
            Generate();
        }

        public virtual void OnPoolableReleased()
        {
            Destroy();
        }

        public virtual void OnPoolableDestroyed()
        {
            Destroy();
        }
    }
}
