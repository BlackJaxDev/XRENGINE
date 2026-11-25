using MemoryPack;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
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
            internal set => SetObjectID(value, publishNotifications: true, persistentIdentity: true);
        }

        private static ConcurrentDictionary<Guid, XRObjectBase> ObjectsCacheInternal { get; } = [];
        public static IReadOnlyDictionary<Guid, XRObjectBase> ObjectsCache => ObjectsCacheInternal;
        private bool _isRegisteredInObjectCache;
        private bool _constructorObjectCachePublicationDeferred;
        private bool _hasPersistentIdentity;

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

        /// <summary>
        /// Starts an independently owned synchronous construction transaction. Nested
        /// ordinary publication scopes join this batch; any enclosing batch does not own it.
        /// Shared process-lifetime factories complete without taking an ownership lease.
        /// </summary>
        public static ObjectCachePublicationScope BeginIndependentObjectCachePublication()
            => new(CurrentObjectCachePublicationScope, independentBatch: true);

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
            CurrentObjectCachePublicationScope?.RecordConstruction(this);
            Generate();
        }

        /// <summary>
        /// False for objects that are reached only through their owner and never resolved by ID,
        /// such as internal collections. They stay out of the global object cache, which holds
        /// every registered object strongly until it is destroyed: an owner discarded without
        /// destroying them would otherwise leave them, and everything their handlers reference,
        /// reachable for the rest of the process. Overrides must return a constant because the
        /// base constructor reads this before derived construction runs.
        /// </summary>
        protected virtual bool ParticipatesInObjectCache => true;

        /// <summary>
        /// Initializes an object without exposing it through the global cache. The most-derived
        /// constructor must enlist it in a publication scope or explicitly publish it.
        /// </summary>
        protected XRObjectBase(bool deferObjectCachePublication)
        {
            CurrentObjectCachePublicationScope?.RecordConstruction(this);
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
            if (ShouldQueueFinalizerDestruction())
                Destroy();
        }

        /// <summary>
        /// Determines whether automatic finalization requests deferred destruction.
        /// Overrides must be nonblocking and must not call application callbacks.
        /// Explicit destruction does not use this hook.
        /// </summary>
        protected virtual bool ShouldQueueFinalizerDestruction() => true;

        public virtual void Generate()
        {
            using ObjectLifetimeMutationLease lifetime = new(this, EObjectLifetimeMutation.Generate);
            ObjectConstructionIdentityGuard.RequireIdentityMutation(this);
            ObjectConstructionIdentityGuard.RequireOwningBatch(this, CurrentObjectCachePublicationScope?.ConstructionIdentityGuard);
            CurrentObjectCachePublicationScope?.RequireGeneratedOrigin(this);
            SetObjectID(Guid.NewGuid(), publishNotifications: false);
            IsDestroyed = false;
            ClearDestroyQueuedFlag();
            _constructorObjectCachePublicationDeferred = false;

            if (_suppressObjectCacheRegistrationDepth > 0 || !ParticipatesInObjectCache)
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
            using ObjectLifetimeMutationLease lifetime = new(this, EObjectLifetimeMutation.Publish);
            ObjectConstructionIdentityGuard.RequireIdentityMutation(this);
            ObjectConstructionIdentityGuard.RequireOwningBatch(this, CurrentObjectCachePublicationScope?.ConstructionIdentityGuard);
            if (CurrentObjectCachePublicationScope?.TryDeferOwnedPublication(this) == true)
                return;
            lock (ObjectCacheMutationLock)
                PublishObjectCacheRegistrationUnderLock();
        }

        /// <summary>Enlists a constructor-deferred object in the active publication batch.</summary>
        protected void JoinDeferredObjectCachePublication()
        {
            using ObjectLifetimeMutationLease lifetime = new(this, EObjectLifetimeMutation.Publish);
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
            using ObjectLifetimeMutationLease lifetime = new(this, EObjectLifetimeMutation.Unpublish);
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
            using ObjectLifetimeMutationLease lifetime = new(this, EObjectLifetimeMutation.Publish);
            ObjectConstructionIdentityGuard.RequireOwningBatch(this,
                CurrentObjectCachePublicationScope?.ConstructionIdentityGuard, requirePublishing: true);
            if (IsDestroyed)
                throw new InvalidOperationException($"Destroyed object '{GetType().FullName}' cannot be published in the global object cache.");

            int tries = 0;
            XRObjectBase? existing;
            while (ObjectsCacheInternal.TryGetValue(ID, out existing) &&
                   !ReferenceEquals(existing, this))
            {
                // Reloaded assets and scene snapshots may coexist with an older instance.
                // Preserve their serialized references without replacing the cache owner.
                if (_hasPersistentIdentity)
                {
                    _isRegisteredInObjectCache = false;
                    _constructorObjectCachePublicationDeferred = false;
                    return;
                }

                // Collision, update ID and try again.
                SetObjectID(Guid.NewGuid(), publishNotifications: false, allowOwnedPublicationCollision: true);
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

        internal static void PublishDeferredObjectCacheBatch(IReadOnlyList<XRObjectBase> objects, bool allowDestroyedMembers = false)
        {
            lock (ObjectCacheMutationLock)
                for (int index = 0; index < objects.Count; index++)
                {
                    XRObjectBase value = objects[index];
                    using ObjectLifetimeMutationLease lifetime = new(value, EObjectLifetimeMutation.Publish);
                    if (value.IsDestroyed)
                    {
                        if (allowDestroyedMembers)
                            continue;
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
            using ObjectLifetimeMutationLease lifetime = new(this, EObjectLifetimeMutation.AbortConstruction);
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

        /// <summary>Aborts a guarded construction only if its owner remains current after cleanup callbacks.</summary>
        protected bool AbortFailedConstruction(Action onAdmitted, Action validate)
        {
            ArgumentNullException.ThrowIfNull(onAdmitted);
            ArgumentNullException.ThrowIfNull(validate);
            using ObjectLifetimeMutationLease lifetime = new(this, EObjectLifetimeMutation.AbortConstruction);
            if (IsDestroyed)
                return false;

            onAdmitted();
            ClearDestroyQueuedFlag();
            OnDestroying();
            validate();
            lock (ObjectCacheMutationLock)
            {
                validate();
                if (_isRegisteredInObjectCache)
                {
                    if (ObjectsCacheInternal.TryGetValue(ID, out XRObjectBase? current) && ReferenceEquals(current, this))
                        ObjectsCacheInternal.TryRemove(ID, out _);
                    _isRegisteredInObjectCache = false;
                }
                _constructorObjectCachePublicationDeferred = false;
                IsDestroyed = true;
            }
            return true;
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

        private void SetObjectID(Guid value, bool publishNotifications, bool persistentIdentity = false,
            bool allowOwnedPublicationCollision = false)
        {
            using ObjectLifetimeMutationLease lifetime = new(this, EObjectLifetimeMutation.Identity);
            ObjectConstructionIdentityGuard.RequireIdentityMutation(this, allowOwnedPublicationCollision);
            if (!allowOwnedPublicationCollision)
                CurrentObjectCachePublicationScope?.RequireConstructionIdentityChange(this);
            Guid previous = _id;
            bool wasRegistered = _isRegisteredInObjectCache;
            if (previous == value)
            {
                SetField(ref _hasPersistentIdentity, persistentIdentity, publishNotifications: false);
                return;
            }
            if (!SetField(ref _id, value, publishNotifications, nameof(ID)))
                return;

            SetField(ref _hasPersistentIdentity, persistentIdentity, publishNotifications: false);

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
            set => SetFieldValidated(ref _name, value, this,
                static (obj, _, _, _) => obj.ValidateNameWrite());
        }

        /// <summary>Checks an opt-in owner before a name field write.</summary>
        protected virtual void ValidateNameWrite() { }

        /// <summary>
        /// True if the object has been destroyed and no longer exists in a valid state.
        /// </summary>
        [YamlIgnore]
        [Browsable(false)]
        [MemoryPackIgnore]
        public bool IsDestroyed { get; private set; } = false;

        private long _pendingDestructionToken;
        private static long s_nextDestructionToken;
        private static readonly ConcurrentQueue<(XRObjectBase Target, long Token)> _objectsToDestroy = new();
        private static readonly ConcurrentQueue<(XRObjectBase Target, long Token)> _deferredObjectsToDestroy = new();
        private static long s_pendingDestructionCount;
        private static int s_drainingPendingDestructions;

        /// <summary>
        /// True after this object has been queued for deferred destruction.
        /// </summary>
        [YamlIgnore]
        [Browsable(false)]
        [MemoryPackIgnore]
        public bool IsDestroyQueued => Volatile.Read(ref _pendingDestructionToken) != 0;

        /// <summary>
        /// Number of current deferred-destruction requests.
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
        /// Processes ready destruction requests. A busy or deferred request stays pending
        /// until the engine calls this method again.
        /// </summary>
        public static void ProcessPendingDestructions()
        {
            if (Interlocked.CompareExchange(ref s_drainingPendingDestructions, 1, 0) != 0)
                return;
            try
            {
                while (_deferredObjectsToDestroy.TryDequeue(out var deferred))
                    _objectsToDestroy.Enqueue(deferred);
                while (_objectsToDestroy.TryDequeue(out var entry))
                {
                    XRObjectBase value = entry.Target;
                    if (Volatile.Read(ref value._pendingDestructionToken) != entry.Token)
                        continue;
                    bool claimed = false;
                    ExceptionDispatchInfo? failure = null;
                    try
                    {
                        claimed = value.TryEnterQueuedDispatch(entry.Token);
                        if (claimed)
                        {
                            try { value.Destroy(true); }
                            catch
                            {
                                // A virtual override can release state before it calls base.
                                // Do not retry that partial cleanup automatically.
                                value.ClearDestroyQueuedFlag(entry.Token);
                                throw;
                            }
                        }
                    }
                    catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
                    finally
                    {
                        try { if (claimed) value.ExitQueuedDispatch(); }
                        catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
                        finally
                        {
                            try
                            {
                                if (Volatile.Read(ref value._pendingDestructionToken) == entry.Token)
                                    _deferredObjectsToDestroy.Enqueue(entry);
                            }
                            catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
                        }
                    }
                    failure?.Throw();
                }
            }
            finally { Volatile.Write(ref s_drainingPendingDestructions, 0); }
        }

        public virtual void Destroy(bool now = false)
        {
            using ObjectLifetimeMutationLease lifetime = new(this,
                now ? EObjectLifetimeMutation.DestroyNow : EObjectLifetimeMutation.QueueDestruction);
            if (IsDestroyed)
                return;

            if (!now)
            {
                if (Volatile.Read(ref _pendingDestructionToken) == 0)
                {
                    long token = Interlocked.Increment(ref s_nextDestructionToken);
                    if (token <= 0)
                        throw new InvalidOperationException("The destruction request token is exhausted.");
                    if (Interlocked.CompareExchange(ref _pendingDestructionToken, token, 0) == 0)
                    {
                        Interlocked.Increment(ref s_pendingDestructionCount);
                        _objectsToDestroy.Enqueue((this, token));
                    }
                }
                return;
            }

            ClearDestroyQueuedFlag();
            try
            {
                if (!(Destroying?.InvokeAllMatch(this) ?? true))
                    return;

                OnDestroying();
                lock (ObjectCacheMutationLock)
                {
                    if (_isRegisteredInObjectCache)
                    {
                        if (ObjectsCacheInternal.TryGetValue(ID, out XRObjectBase? current) && ReferenceEquals(current, this))
                            ObjectsCacheInternal.TryRemove(ID, out _);
                        _isRegisteredInObjectCache = false;
                    }
                    IsDestroyed = true;
                }

                Destroyed?.Invoke(this);
            }
            catch
            {
                CurrentObjectCachePublicationScope?.RecordDestructionFailure(this);
                throw;
            }
        }

        private void ClearDestroyQueuedFlag()
        {
            if (Interlocked.Exchange(ref _pendingDestructionToken, 0) != 0)
                Interlocked.Decrement(ref s_pendingDestructionCount);
        }

        private void ClearDestroyQueuedFlag(long token)
        {
            if (Interlocked.CompareExchange(ref _pendingDestructionToken, 0, token) == token)
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
