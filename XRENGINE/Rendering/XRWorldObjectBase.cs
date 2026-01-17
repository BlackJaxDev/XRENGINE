using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using MemoryPack;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering;
using YamlDotNet.Serialization;

namespace XREngine
{
    /// <summary>
    /// Indicates that this property should be replicated to the network on every tick.
    /// Recommended for properties that change frequently.
    /// The SetField method is not required to change the property value.
    /// </summary>
    /// <param name="udp"></param>
    [AttributeUsage(AttributeTargets.Property)]
    public class ReplicateOnTickAttribute(bool compress = false) : Attribute 
    {
        public bool Compress { get; } = compress;
    }

    /// <summary>
    /// Indicates that this property should be replicated to the network when it changes.
    /// Recommended for properties that change infrequently.
    /// MUST use the SetField method to change the property value.
    /// </summary>
    /// <param name="udp"></param>
    [AttributeUsage(AttributeTargets.Property)]
    public class ReplicateOnChangeAttribute(bool compress = false) : Attribute
    {
        public bool Compress { get; } = compress;
    }

    [Serializable]
    /// <summary>
    /// This base class is for any object located within a world instance.
    /// </summary>
    public abstract class XRWorldObjectBase : XRObjectBase
    {
        private static readonly ConcurrentDictionary<Type, ReplicationInfo> _replicatedTypes = [];
        public class ReplicationInfo
        {
            internal readonly Dictionary<string, PropertyInfo> _replicateOnChangeProperties = [];
            internal readonly Dictionary<string, PropertyInfo> _replicateOnTickProperties = [];
            internal List<string> _compressedPropertyNames = [];

            public IReadOnlyDictionary<string, PropertyInfo> ReplicateOnChangeProperties => _replicateOnChangeProperties;
            public IReadOnlyDictionary<string, PropertyInfo> ReplicateOnTickProperties => _replicateOnTickProperties;
            public IReadOnlyList<string> CompressedPropertyNames => _compressedPropertyNames;
        }

        private object? _userData;
        [MemoryPackIgnore]
        public object? UserData
        {
            get => _userData;
            set => SetField(ref _userData, value);
        }

        public ReplicationInfo? GetReplicationInfo()
            => _replicatedTypes.TryGetValue(GetType(), out var repl) ? repl : null;

        public bool IsReplicateOnChangeProperty(string propName)
            => _replicatedTypes.TryGetValue(GetType(), out var repl) && repl.ReplicateOnChangeProperties.ContainsKey(propName);
        public bool IsReplicateOnTickProperty(string propName)
            => _replicatedTypes.TryGetValue(GetType(), out var repl) && repl.ReplicateOnTickProperties.ContainsKey(propName);

        private int _owningServerPlayerIndex = -1;
        [YamlIgnore]
        [Browsable(false)]
        [Category("Networking")]
        public int OwningPlayerServerIndex
        {
            get => _owningServerPlayerIndex;
            set => SetField(ref _owningServerPlayerIndex, value);
        }

        /// <summary>
        /// If true, this object has authority over its properties and will replicate them to all clients, either through the server if a client, or directly if the server or a p2p client.
        /// If false, this object will not replicate its properties to the network and will only receive updates from the server.
        /// </summary>
        [YamlIgnore]
        [Category("Networking")]
        public bool HasNetworkAuthority
            => IsNotAClient() || ALocalClientPlayerOwnsThis();

        private bool ALocalClientPlayerOwnsThis()
            => Engine.State.LocalPlayers.Any(p => p is not null && OwningPlayerServerIndex >= 0 && OwningPlayerServerIndex == p.PlayerInfo.ServerIndex);

        private static bool IsNotAClient()
            => !(Engine.Networking?.IsClient ?? true);

        static XRWorldObjectBase()
        {
            CollectReplicableProperties();
        }

        private static void CollectReplicableProperties()
        {
            _replicatedTypes.Clear();
            BindingFlags replicableFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            //Get all types deriving from XRWorldObjectBase
            var baseType = typeof(XRWorldObjectBase);
            var allTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Return only the types that were successfully loaded
                    return ex.Types.Where(t => t != null).Cast<Type>();
                }
                catch
                {
                    return Enumerable.Empty<Type>();
                }
            });
            //var derivedTypes = allTypes.Where(t => t.IsAssignableTo(baseType));
            //var allProperties = derivedTypes.SelectMany(t => t.GetProperties(replicableFlags));
            void TestType(Type t)
            {
                if (!t.IsAssignableTo(baseType))
                    return;

                var props = t.GetProperties(replicableFlags);
                if (props.Length == 0)
                    return;

                ReplicationInfo? repl = null;
                foreach (var prop in props)
                {
                    if (prop.GetCustomAttribute<ReplicateOnChangeAttribute>(true) is ReplicateOnChangeAttribute changeAttr)
                    {
                        repl ??= new ReplicationInfo();
                        repl._replicateOnChangeProperties.Add(prop.Name, prop);
                        if (changeAttr.Compress)
                            repl._compressedPropertyNames.Add(prop.Name);
                    }
                    if (prop.GetCustomAttribute<ReplicateOnTickAttribute>(true) is ReplicateOnTickAttribute tickAttr)
                    {
                        repl ??= new ReplicationInfo();
                        repl._replicateOnTickProperties.Add(prop.Name, prop);
                        if (tickAttr.Compress)
                            repl._compressedPropertyNames.Add(prop.Name);
                    }
                }

                if (repl is not null)
                    _replicatedTypes.AddOrUpdate(t, repl, (k, v) => repl);
            }
            Parallel.ForEach(allTypes, TestType);
        }

        internal XRWorldInstance? _world = null;
        [YamlIgnore]
        [Browsable(false)]
        [MemoryPackIgnore]
        public XRWorldInstance? World
        {
            get => _world;
            internal set => SetField(ref _world, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            ReplicationInfo? repl = World is not null ? GetReplicationInfo() : null;

            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(World):
                    if (World is not null)
                    {
                        if (_tickCache is not null)
                            foreach (var (group, order, tick) in _tickCache)
                                World?.RegisterTick(group, order, tick);
                        if (repl is not null && repl.ReplicateOnTickProperties.Count > 0)
                            RegisterTick(ETickGroup.Late, ETickOrder.Logic, BroadcastTickedProperties);
                    }
                    break;
            }

            //TODO: resend on failed ack should be false if we're streaming data to this property
            //(add a new attribute, or determine based on time since last set)
            if (World is not null && repl is not null && propName is not null && repl.ReplicateOnChangeProperties.TryGetValue(propName, out var pair))
                EnqueuePropertyReplication(propName, field, repl.CompressedPropertyNames.Contains(propName), true);
        }

        [Browsable(false)]
        [Category("Networking")]
        [MemoryPackIgnore]
        public float LastTickReplicationTime { get; private set; } = 0;
        [Category("Networking")]
        public float TimeBetweenReplications
        {
            get => _timeBetweenReplicationsOverride ?? Engine.EffectiveSettings.TimeBetweenReplications;
            set => SetField(ref _timeBetweenReplicationsOverride, value);
        }

        private float? _timeBetweenReplicationsOverride = null;

        private void BroadcastTickedProperties()
        {
            var repl = GetReplicationInfo();
            if (repl is null)
                return;

            float now = Engine.Time.Timer.Time();
            float elapsed = now - LastTickReplicationTime;
            if (elapsed < TimeBetweenReplications)
                return;

            LastTickReplicationTime = now;

            //TODO: group all ticked properties into a single packet
            foreach (var prop in repl.ReplicateOnTickProperties.Values)
                EnqueuePropertyReplication(prop.Name, prop.GetValue(this), repl.CompressedPropertyNames.Contains(prop.Name), false);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(World):
                        if (World is not null)
                        {
                            if (_tickCache is not null)
                                foreach (var (group, order, tick) in _tickCache)
                                    World?.UnregisterTick(group, order, tick);
                        }
                        break;
                }
            }
            return change;
        }

        private readonly ConcurrentHashSet<(ETickGroup group, int order, Engine.TickList.DelTick tick)> _tickCache = [];

        public void RegisterTick(ETickGroup group, int order, Engine.TickList.DelTick tick)
        {
            _tickCache.Add((group, order, tick));
            World?.RegisterTick(group, order, tick);
        }
        public void UnregisterTick(ETickGroup group, int order, Engine.TickList.DelTick tick)
        {
            _tickCache.TryRemove((group, order, tick));
            World?.UnregisterTick(group, order, tick);
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();
            ClearTicks();
        }

        protected internal void ClearTicks()
        {
            foreach (var (group, order, tick) in _tickCache)
                World?.UnregisterTick(group, order, tick);
            _tickCache.Clear();
        }

        public void RegisterTick(ETickGroup group, ETickOrder order, Engine.TickList.DelTick tick)
            => RegisterTick(group, (int)order, tick);
        public void UnregisterTick(ETickGroup group, ETickOrder order, Engine.TickList.DelTick tick)
            => UnregisterTick(group, (int)order, tick);

        public void RegisterAnimationTick(Action<XRWorldObjectBase> tick, ETickGroup group = ETickGroup.Normal)
            => RegisterTick(group, ETickOrder.Animation, () => tick(this));
        public void RegisterAnimationTick<T>(Action<T> tick, ETickGroup group = ETickGroup.Normal) where T : XRWorldObjectBase
            => RegisterTick(group, ETickOrder.Animation, () => tick((T)this));

        public void UnregisterAnimationTick(Action<XRWorldObjectBase> tick, ETickGroup group = ETickGroup.Normal)
            => UnregisterTick(group, ETickOrder.Animation, () => tick(this));
        public void UnregisterAnimationTick<T>(Action<T> tick, ETickGroup group = ETickGroup.Normal) where T : XRWorldObjectBase
            => UnregisterTick(group, ETickOrder.Animation, () => tick((T)this));

        /// <summary>
        /// Tells the engine to replicate this object to the network.
        /// </summary>
        /// <param name="udp"></param>
        public void EnqueueSelfReplication(bool compress, bool resendOnFailedAck)
            => Engine.Networking?.ReplicateObject(this, compress, resendOnFailedAck);
        /// <summary>
        /// Tells the engine to replicate a specific property to the network.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="propName"></param>
        /// <param name="value"></param>
        /// <param name="udp"></param>
        public void EnqueuePropertyReplication<T>(string? propName, T value, bool compress, bool resendOnFailedAck)
            => Engine.Networking?.ReplicatePropertyUpdated(this, propName, value, compress, resendOnFailedAck);
        /// <summary>
        /// Tells the engine to replicate some data to the network.
        /// </summary>
        /// <param name="id"></param>
        /// <param name="data"></param>
        /// <param name="udp"></param>
        public void EnqueueDataReplication(string id, byte[] data, bool compress, bool resendOnFailedAck)
            => Engine.Networking?.ReplicateData(this, data, id, compress, resendOnFailedAck);

        /// <summary>
        /// Called by data replication to receive generic data from the network.
        /// Must be overridden to handle the data.
        /// </summary>
        /// <param name="id"></param>
        /// <param name="data"></param>
        public virtual void ReceiveData(string id, object? data)
        {

        }

        /// <summary>
        /// Called by full replication to copy all properties from the replicated object.
        /// </summary>
        /// <param name="newObj"></param>
        public virtual void CopyFrom(XRWorldObjectBase newObj)
        {
            var type = GetType();
            var newObjsType = newObj.GetType();
            if (newObjsType != type)
                return;

            var props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            foreach (var prop in props)
                if (prop.CanRead && prop.CanWrite && prop.GetSetMethod(true) is not null)
                    prop.SetValue(this, prop.GetValue(newObj));
        }

        /// <summary>
        /// Called by property replication to set a specific property from the network.
        /// </summary>
        /// <param name="propName"></param>
        /// <param name="value"></param>
        public void SetReplicatedProperty(string propName, object? value)
        {
            var repl = GetReplicationInfo();
            if (repl is null)
                return;

            if (repl.ReplicateOnChangeProperties.TryGetValue(propName, out var changeProp) && IsSettable(value, changeProp))
                changeProp.SetValue(this, value);

            if (repl.ReplicateOnTickProperties.TryGetValue(propName, out var tickProp) && IsSettable(value, tickProp))
                tickProp.SetValue(this, value);
        }

        private static bool IsSettable(object? value, PropertyInfo pair)
        {
            return value is null
                ? pair.PropertyType.IsClass && !pair.PropertyType.IsValueType
                : pair.PropertyType.IsAssignableFrom(value.GetType());
        }
    }
}
