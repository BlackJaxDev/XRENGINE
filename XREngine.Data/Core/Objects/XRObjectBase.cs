using MemoryPack;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading;
using XREngine.Core;
using YamlDotNet.Serialization;

namespace XREngine.Data.Core
{
    /// <summary>
    /// This base class is for any object that is managed by the engine, has a unique ID, and should be destroyed after use.
    /// </summary>
    [Serializable]
    [MemoryPackable(GenerateType.NoGenerate)]
    public abstract partial class XRObjectBase : XRBase, IDisposable, IPoolable
    {
        [ThreadStatic]
        private static int _suppressObjectCacheRegistrationDepth;

        [ThreadStatic]
        private static ObjectCachePublicationScope? _currentObjectCachePublicationScope;

        private static readonly Lock ObjectCacheMutationLock = new();

        private Guid _id = Guid.NewGuid();

        [Browsable(false)]
        public Guid ID
        {
            get => _id;
            internal set => SetObjectID(value, publishNotifications: true);
        }

        private static ConcurrentDictionary<Guid, XRObjectBase> ObjectsCacheInternal { get; } = [];
        public static IReadOnlyDictionary<Guid, XRObjectBase> ObjectsCache => ObjectsCacheInternal;
        private bool _isRegisteredInObjectCache;
        private bool _constructorObjectCachePublicationDeferred;

        internal static ObjectCachePublicationScope? CurrentObjectCachePublicationScope
        {
            get => _currentObjectCachePublicationScope;
            set => _currentObjectCachePublicationScope = value;
        }

        public static IDisposable SuppressObjectCacheRegistration()
            => new ObjectCacheRegistrationSuppressionScope();

        /// <summary>
        /// Starts a synchronous transaction that withholds newly generated objects from
        /// global discovery until the root scope completes.
        /// </summary>
        public static ObjectCachePublicationScope BeginDeferredObjectCachePublication()
            => new(CurrentObjectCachePublicationScope);

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

        public XRObjectBase() => Generate();

        /// <summary>
        /// Initializes an object without exposing it through the global cache. The most-derived
        /// constructor must enlist it in a publication scope or explicitly publish it.
        /// </summary>
        protected XRObjectBase(bool deferObjectCachePublication)
        {
            if (!deferObjectCachePublication)
            {
                Generate();
                return;
            }

            SetObjectID(Guid.NewGuid(), publishNotifications: false);
            IsDestroyed = false;
            ClearDestroyQueuedFlag();
            _isRegisteredInObjectCache = false;
            _constructorObjectCachePublicationDeferred = true;
        }
        ~XRObjectBase()
        {
            Destroy();
        }

        public virtual void Generate()
        {
            SetObjectID(Guid.NewGuid(), publishNotifications: false);
            IsDestroyed = false;
            ClearDestroyQueuedFlag();
            _constructorObjectCachePublicationDeferred = false;

            if (_suppressObjectCacheRegistrationDepth > 0)
            {
                _isRegisteredInObjectCache = false;
                return;
            }

            if (CurrentObjectCachePublicationScope is { } publicationScope)
            {
                _isRegisteredInObjectCache = false;
                publicationScope.Enlist(this);
                return;
            }

            PublishObjectCacheRegistration();
        }

        protected void PublishObjectCacheRegistration()
        {
            lock (ObjectCacheMutationLock)
                PublishObjectCacheRegistrationUnderLock();
        }

        /// <summary>Enlists a constructor-deferred object in the active publication batch.</summary>
        protected void JoinDeferredObjectCachePublication()
        {
            if (!_constructorObjectCachePublicationDeferred)
                return;

            ObjectCachePublicationScope publicationScope = CurrentObjectCachePublicationScope ??
                throw new InvalidOperationException("A constructor-deferred object requires an active object-cache publication scope.");
            publicationScope.Enlist(this);
            _constructorObjectCachePublicationDeferred = false;
        }

        /// <summary>Removes this object from global discovery until derived construction completes.</summary>
        protected void DeferExistingObjectCachePublication()
        {
            lock (ObjectCacheMutationLock)
            {
                if (!_isRegisteredInObjectCache)
                    return;

                if (ObjectsCacheInternal.TryGetValue(ID, out XRObjectBase? current) &&
                    ReferenceEquals(current, this))
                {
                    ObjectsCacheInternal.TryRemove(ID, out _);
                }
                _isRegisteredInObjectCache = false;
            }
        }

        protected virtual void PublishObjectCacheRegistrationUnderLock()
        {
            if (IsDestroyed)
                throw new InvalidOperationException($"Destroyed object '{GetType().FullName}' cannot be published in the global object cache.");

            int tries = 0;
            XRObjectBase? existing;
            while (ObjectsCacheInternal.TryGetValue(ID, out existing) &&
                   !ReferenceEquals(existing, this))
            {
                // Collision, update ID and try again.
                SetObjectID(Guid.NewGuid(), publishNotifications: false);
                if (tries++ > 10)
                    throw new Exception("Failed to generate a unique ID for an object."); // Highly unlikely.
            }

            if (ObjectsCacheInternal.TryGetValue(ID, out existing) && ReferenceEquals(existing, this))
            {
                _isRegisteredInObjectCache = true;
                _constructorObjectCachePublicationDeferred = false;
                return;
            }

            if (!ObjectsCacheInternal.TryAdd(ID, this))
                throw new InvalidOperationException($"Failed to publish object '{GetType().FullName}' in the global object cache.");

            _isRegisteredInObjectCache = true;
            _constructorObjectCachePublicationDeferred = false;
        }

        internal static void PublishDeferredObjectCacheBatch(IReadOnlyList<XRObjectBase> objects)
        {
            lock (ObjectCacheMutationLock)
                for (int index = 0; index < objects.Count; index++)
                {
                    XRObjectBase value = objects[index];
                    if (value.IsDestroyed)
                    {
                        throw new InvalidOperationException(
                            $"Object-cache publication batch contains destroyed member '{value.GetType().FullName}'.");
                    }

                    value.PublishObjectCacheRegistrationUnderLock();
                }
        }

        internal static void AbortDeferredObjectCachePublication(IReadOnlyList<XRObjectBase> objects)
        {
            for (int index = objects.Count - 1; index >= 0; index--)
            {
                XRObjectBase value = objects[index];
                try
                {
                    value.AbortFailedConstruction();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError(
                        "Deferred construction cleanup failed for {0}: {1}",
                        value.GetType().FullName,
                        ex);
                }

                try
                {
                    ((IDisposable)value).Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError(
                        "Deferred construction disposal failed for {0}: {1}",
                        value.GetType().FullName,
                        ex);
                }
            }
        }

        /// <summary>
        /// Terminates a constructor that failed after base registration without invoking
        /// vetoable public destruction callbacks.
        /// </summary>
        protected void AbortFailedConstruction()
        {
            if (IsDestroyed)
                return;

            ClearDestroyQueuedFlag();
            try
            {
                OnDestroying();
            }
            finally
            {
                lock (ObjectCacheMutationLock)
                {
                    if (_isRegisteredInObjectCache)
                    {
                        ObjectsCacheInternal.TryRemove(ID, out _);
                        _isRegisteredInObjectCache = false;
                    }
                    _constructorObjectCachePublicationDeferred = false;
                    IsDestroyed = true;
                }
            }
        }

        /// <summary>
        /// Adopts a stable identifier derived from persistent source identity.
        /// Importers use this before serialization so unchanged object graphs retain
        /// coherent references across independent import runs.
        /// </summary>
        /// <param name="persistentID">The non-empty persistent identifier to adopt.</param>
        public void AdoptPersistentID(Guid persistentID)
        {
            if (persistentID == Guid.Empty)
                throw new ArgumentException("A persistent object ID cannot be empty.", nameof(persistentID));

            ID = persistentID;
        }

        private void SetObjectID(Guid value, bool publishNotifications)
        {
            Guid previous = _id;
            bool wasRegistered = _isRegisteredInObjectCache;
            if (!SetField(ref _id, value, publishNotifications, nameof(ID)))
                return;

            if (!wasRegistered)
                return;

            if (ObjectsCacheInternal.TryGetValue(previous, out XRObjectBase? existing) &&
                ReferenceEquals(existing, this))
            {
                ObjectsCacheInternal.TryRemove(previous, out _);
            }

            lock (ObjectCacheMutationLock)
                _isRegisteredInObjectCache = ObjectsCacheInternal.TryAdd(value, this);
        }

        /// <summary>
        /// Event that is called when the object is destroyed.
        /// </summary>
        [YamlIgnore]
        [MemoryPackIgnore]
        public XREvent<XRObjectBase>? Destroyed;
        /// <summary>
        /// Event that is called when the object has been requested to be destroyed.
        /// All listeners must return true for the object to be destroyed.
        /// </summary>
        [YamlIgnore]
        [MemoryPackIgnore]
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
        [Browsable(false)]
        [MemoryPackIgnore]
        public bool IsDestroyed { get; private set; } = false;

        private int _destroyQueued;
        private static readonly ConcurrentQueue<XRObjectBase> _objectsToDestroy = new();
        private static long s_pendingDestructionCount;

        /// <summary>
        /// True after this object has been queued for deferred destruction.
        /// </summary>
        [YamlIgnore]
        [Browsable(false)]
        [MemoryPackIgnore]
        public bool IsDestroyQueued => Volatile.Read(ref _destroyQueued) != 0;

        /// <summary>
        /// Number of objects waiting in the global deferred-destruction queue.
        /// </summary>
        [YamlIgnore]
        [Browsable(false)]
        [MemoryPackIgnore]
        public static long PendingDestructionCount
        {
            get
            {
                long count = Interlocked.Read(ref s_pendingDestructionCount);
                return count < 0 ? 0 : count;
            }
        }

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
                if (Interlocked.Exchange(ref _destroyQueued, 1) == 0)
                {
                    Interlocked.Increment(ref s_pendingDestructionCount);
                    _objectsToDestroy.Enqueue(this);
                }
                return;
            }

            ClearDestroyQueuedFlag();
            if (!(Destroying?.InvokeAllMatch(this) ?? true))
                return;

            OnDestroying();
            lock (ObjectCacheMutationLock)
            {
                if (_isRegisteredInObjectCache)
                {
                    ObjectsCacheInternal.Remove(ID, out _);
                    _isRegisteredInObjectCache = false;
                }
                IsDestroyed = true;
            }

            Destroyed?.Invoke(this);
        }

        private void ClearDestroyQueuedFlag()
        {
            if (Interlocked.Exchange(ref _destroyQueued, 0) == 1)
                Interlocked.Decrement(ref s_pendingDestructionCount);
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

        private readonly struct ObjectCacheRegistrationSuppressionScope : IDisposable
        {
            public ObjectCacheRegistrationSuppressionScope()
                => _suppressObjectCacheRegistrationDepth++;

            public void Dispose()
                => _suppressObjectCacheRegistrationDepth--;
        }
    }
}
