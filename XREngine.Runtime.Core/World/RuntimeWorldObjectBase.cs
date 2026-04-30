using System.Collections.Concurrent;
using System.ComponentModel;
using System.Numerics;
using System.Reflection;
using MemoryPack;
using XREngine.Components;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine;

[AttributeUsage(AttributeTargets.Property)]
public sealed class ReplicateOnTickAttribute(bool compress = false) : Attribute
{
    public bool Compress { get; } = compress;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class ReplicateOnChangeAttribute(bool compress = false) : Attribute
{
    public bool Compress { get; } = compress;
}

public interface IRuntimeWorldContext
{
    bool IsPlaySessionActive { get; }
    void RegisterTick(ETickGroup group, int order, WorldTick tick);
    void UnregisterTick(ETickGroup group, int order, WorldTick tick);
    void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject);
    void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix);
}

public interface IRuntimeWorldObjectServices
{
    bool IsClient { get; }
    float CurrentTimeSeconds { get; }
    float DefaultTimeBetweenReplications { get; }
    bool HasLocalPlayerAuthority(int owningPlayerServerIndex);
    void OnRuntimeObjectActivated(RuntimeWorldObjectBase worldObject);
    void OnRuntimeObjectDeactivated(RuntimeWorldObjectBase worldObject);
    void OnRuntimeObjectWorldChanged(RuntimeWorldObjectBase worldObject, IRuntimeWorldContext? worldContext, bool isActiveInHierarchy);
    void ReplicateObject(RuntimeWorldObjectBase worldObject, bool compress, bool resendOnFailedAck, float maxAckWaitSec);
    void ReplicatePropertyUpdated<T>(RuntimeWorldObjectBase worldObject, string? propertyName, T value, bool compress, bool resendOnFailedAck, float maxAckWaitSec);
    void ReplicateData(RuntimeWorldObjectBase worldObject, byte[] data, string id, bool compress, bool resendOnFailedAck, float maxAckWaitSec);
}

public static class RuntimeWorldObjectServices
{
    public static IRuntimeWorldObjectServices? Current { get; set; }
}

[Serializable]
public abstract class RuntimeWorldObjectBase : XRObjectBase
{
    private static readonly ConcurrentDictionary<Type, ReplicationInfo> ReplicatedTypes = [];

    public sealed class ReplicationInfo
    {
        internal readonly Dictionary<string, PropertyInfo> ReplicateOnChangePropertiesInternal = [];
        internal readonly Dictionary<string, PropertyInfo> ReplicateOnTickPropertiesInternal = [];
        internal List<string> CompressedPropertyNamesInternal = [];

        public IReadOnlyDictionary<string, PropertyInfo> ReplicateOnChangeProperties => ReplicateOnChangePropertiesInternal;
        public IReadOnlyDictionary<string, PropertyInfo> ReplicateOnTickProperties => ReplicateOnTickPropertiesInternal;
        public IReadOnlyList<string> CompressedPropertyNames => CompressedPropertyNamesInternal;
    }

    private object? _userData;

    [MemoryPackIgnore]
    public object? UserData
    {
        get => _userData;
        set => SetField(ref _userData, value);
    }

    private int _owningServerPlayerIndex = -1;
    private IRuntimeWorldContext? _world;
    private float? _timeBetweenReplicationsOverride;
    private readonly ConcurrentHashSet<(ETickGroup group, int order, WorldTick tick)> _tickCache = [];

    static RuntimeWorldObjectBase()
    {
        InitializeReplicationMetadata();
    }

    [YamlIgnore]
    [Browsable(false)]
    [MemoryPackIgnore]
    public IRuntimeWorldContext? World
    {
        get => _world;
        internal protected set => SetField(ref _world, value);
    }

    public TWorld? WorldAs<TWorld>() where TWorld : class, IRuntimeWorldContext
        => World as TWorld;

    [YamlIgnore]
    [Browsable(false)]
    [Category("Networking")]
    public int OwningPlayerServerIndex
    {
        get => _owningServerPlayerIndex;
        set => SetField(ref _owningServerPlayerIndex, value);
    }

    [YamlIgnore]
    [Category("Networking")]
    public bool HasNetworkAuthority
        => IsNotAClient() || ALocalClientPlayerOwnsThis();

    [Browsable(false)]
    [Category("Networking")]
    [MemoryPackIgnore]
    public float LastTickReplicationTime { get; private set; }

    [Category("Networking")]
    [YamlIgnore]
    public float TimeBetweenReplications
    {
        get => _timeBetweenReplicationsOverride ?? RuntimeWorldObjectServices.Current?.DefaultTimeBetweenReplications ?? 0.0f;
        set => SetField(ref _timeBetweenReplicationsOverride, value);
    }

    [Browsable(false)]
    [YamlMember(Alias = "TimeBetweenReplications")]
    public float? TimeBetweenReplicationsOverrideSerialized
    {
        get => _timeBetweenReplicationsOverride;
        set => SetField(ref _timeBetweenReplicationsOverride, value);
    }

    public ReplicationInfo? GetReplicationInfo()
        => ReplicatedTypes.TryGetValue(GetType(), out ReplicationInfo? replication) ? replication : null;

    public bool IsReplicateOnChangeProperty(string propertyName)
        => ReplicatedTypes.TryGetValue(GetType(), out ReplicationInfo? replication) && replication.ReplicateOnChangeProperties.ContainsKey(propertyName);

    public bool IsReplicateOnTickProperty(string propertyName)
        => ReplicatedTypes.TryGetValue(GetType(), out ReplicationInfo? replication) && replication.ReplicateOnTickProperties.ContainsKey(propertyName);

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        ReplicationInfo? replication = World is not null ? GetReplicationInfo() : null;

        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(World):
                if (World is not null)
                {
                    foreach ((ETickGroup group, int order, WorldTick tick) in _tickCache)
                        World.RegisterTick(group, order, tick);

                    if (replication is not null && replication.ReplicateOnTickProperties.Count > 0)
                        RegisterTick(ETickGroup.Late, ETickOrder.Logic, BroadcastTickedProperties);
                }
                break;
        }

        if (World is not null
            && replication is not null
            && propName is not null
            && replication.ReplicateOnChangeProperties.TryGetValue(propName, out _))
        {
            EnqueuePropertyReplication(propName, field, replication.CompressedPropertyNames.Contains(propName), resendOnFailedAck: true);
        }
    }

    protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
    {
        bool changed = base.OnPropertyChanging(propName, field, @new);
        if (changed && propName == nameof(World) && World is not null)
        {
            foreach ((ETickGroup group, int order, WorldTick tick) in _tickCache)
                World.UnregisterTick(group, order, tick);
        }

        return changed;
    }

    protected override void OnDestroying()
    {
        base.OnDestroying();
        ClearTicks();
    }

    public void RegisterTick(ETickGroup group, int order, WorldTick tick)
    {
        _tickCache.Add((group, order, tick));
        World?.RegisterTick(group, order, tick);
    }

    public void UnregisterTick(ETickGroup group, int order, WorldTick tick)
    {
        _tickCache.TryRemove((group, order, tick));
        World?.UnregisterTick(group, order, tick);
    }

    public void RegisterTick(ETickGroup group, ETickOrder order, WorldTick tick)
        => RegisterTick(group, (int)order, tick);

    public void UnregisterTick(ETickGroup group, ETickOrder order, WorldTick tick)
        => UnregisterTick(group, (int)order, tick);

    public void RegisterAnimationTick(Action<RuntimeWorldObjectBase> tick, ETickGroup group = ETickGroup.Normal)
        => RegisterTick(group, ETickOrder.Animation, () => tick(this));

    public void RegisterAnimationTick<T>(Action<T> tick, ETickGroup group = ETickGroup.Normal) where T : RuntimeWorldObjectBase
        => RegisterTick(group, ETickOrder.Animation, () => tick((T)this));

    public void UnregisterAnimationTick(Action<RuntimeWorldObjectBase> tick, ETickGroup group = ETickGroup.Normal)
        => UnregisterTick(group, ETickOrder.Animation, () => tick(this));

    public void UnregisterAnimationTick<T>(Action<T> tick, ETickGroup group = ETickGroup.Normal) where T : RuntimeWorldObjectBase
        => UnregisterTick(group, ETickOrder.Animation, () => tick((T)this));

    public void EnqueueSelfReplication(bool compress, bool resendOnFailedAck, float maxAckWaitSec = 5.0f)
        => RuntimeWorldObjectServices.Current?.ReplicateObject(this, compress, resendOnFailedAck, maxAckWaitSec);

    public void EnqueuePropertyReplication<T>(string? propertyName, T value, bool compress, bool resendOnFailedAck, float maxAckWaitSec = 5.0f)
        => RuntimeWorldObjectServices.Current?.ReplicatePropertyUpdated(this, propertyName, value, compress, resendOnFailedAck, maxAckWaitSec);

    public void EnqueueDataReplication(string id, byte[] data, bool compress, bool resendOnFailedAck, float maxAckWaitSec = 5.0f)
        => RuntimeWorldObjectServices.Current?.ReplicateData(this, data, id, compress, resendOnFailedAck, maxAckWaitSec);

    public virtual void ReceiveData(string id, object? data)
    {
    }

    public virtual void CopyFrom(RuntimeWorldObjectBase newObj)
    {
        Type type = GetType();
        if (newObj.GetType() != type)
            return;

        PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        foreach (PropertyInfo property in properties)
        {
            if (property.CanRead && property.CanWrite && property.GetSetMethod(true) is not null)
                property.SetValue(this, property.GetValue(newObj));
        }
    }

    public void SetReplicatedProperty(string propertyName, object? value)
    {
        ReplicationInfo? replication = GetReplicationInfo();
        if (replication is null)
            return;

        if (replication.ReplicateOnChangeProperties.TryGetValue(propertyName, out PropertyInfo? changeProperty) && IsSettable(value, changeProperty))
            changeProperty.SetValue(this, value);

        if (replication.ReplicateOnTickProperties.TryGetValue(propertyName, out PropertyInfo? tickProperty) && IsSettable(value, tickProperty))
            tickProperty.SetValue(this, value);
    }

    public void ClearTicks()
    {
        foreach ((ETickGroup group, int order, WorldTick tick) in _tickCache)
            World?.UnregisterTick(group, order, tick);

        _tickCache.Clear();
    }

    public void SetWorldContext(IRuntimeWorldContext? worldContext)
        => World = worldContext;

    private static void InitializeReplicationMetadata()
    {
        if (TryLoadReplicablePropertiesFromAotMetadata())
            return;

        CollectReplicableProperties();
    }

    private static bool TryLoadReplicablePropertiesFromAotMetadata()
    {
        if (!XRRuntimeEnvironment.IsAotRuntimeBuild)
            return false;

        AotRuntimeMetadata? metadata = AotRuntimeMetadataStore.Metadata;
        if (metadata?.WorldObjectReplications is null || metadata.WorldObjectReplications.Length == 0)
            return false;

        ReplicatedTypes.Clear();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (AotWorldObjectReplicationInfo entry in metadata.WorldObjectReplications)
        {
            Type? type = AotRuntimeMetadataStore.ResolveType(entry.AssemblyQualifiedName);
            if (type is null || !typeof(RuntimeWorldObjectBase).IsAssignableFrom(type))
                continue;

            ReplicationInfo replication = new();
            foreach (string propertyName in entry.ReplicateOnChangeProperties)
            {
                PropertyInfo? property = type.GetProperty(propertyName, flags);
                if (property is not null)
                    replication.ReplicateOnChangePropertiesInternal[property.Name] = property;
            }

            foreach (string propertyName in entry.ReplicateOnTickProperties)
            {
                PropertyInfo? property = type.GetProperty(propertyName, flags);
                if (property is not null)
                    replication.ReplicateOnTickPropertiesInternal[property.Name] = property;
            }

            replication.CompressedPropertyNamesInternal = [.. entry.CompressedPropertyNames.Distinct(StringComparer.Ordinal)];

            if (replication.ReplicateOnChangePropertiesInternal.Count > 0 || replication.ReplicateOnTickPropertiesInternal.Count > 0)
                ReplicatedTypes[type] = replication;
        }

        return true;
    }

    private static void CollectReplicableProperties()
    {
        ReplicatedTypes.Clear();
        const BindingFlags replicableFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type baseType = typeof(RuntimeWorldObjectBase);
        IEnumerable<Type> allTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(static assembly =>
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(static type => type is not null).Cast<Type>();
            }
            catch
            {
                return Enumerable.Empty<Type>();
            }
        });

        void TestType(Type type)
        {
            if (!type.IsAssignableTo(baseType))
                return;

            PropertyInfo[] properties = type.GetProperties(replicableFlags);
            if (properties.Length == 0)
                return;

            ReplicationInfo? replication = null;
            foreach (PropertyInfo property in properties)
            {
                if (property.GetCustomAttribute<ReplicateOnChangeAttribute>(true) is ReplicateOnChangeAttribute changeAttr)
                {
                    replication ??= new ReplicationInfo();
                    replication.ReplicateOnChangePropertiesInternal.Add(property.Name, property);
                    if (changeAttr.Compress)
                        replication.CompressedPropertyNamesInternal.Add(property.Name);
                }

                if (property.GetCustomAttribute<ReplicateOnTickAttribute>(true) is ReplicateOnTickAttribute tickAttr)
                {
                    replication ??= new ReplicationInfo();
                    replication.ReplicateOnTickPropertiesInternal.Add(property.Name, property);
                    if (tickAttr.Compress)
                        replication.CompressedPropertyNamesInternal.Add(property.Name);
                }
            }

            if (replication is not null)
                ReplicatedTypes.AddOrUpdate(type, replication, (_, _) => replication);
        }

        Parallel.ForEach(allTypes, TestType);
    }

    private bool ALocalClientPlayerOwnsThis()
        => RuntimeWorldObjectServices.Current?.HasLocalPlayerAuthority(OwningPlayerServerIndex) ?? false;

    private static bool IsNotAClient()
        => !(RuntimeWorldObjectServices.Current?.IsClient ?? true);

    private void BroadcastTickedProperties()
    {
        ReplicationInfo? replication = GetReplicationInfo();
        if (replication is null)
            return;

        float now = RuntimeWorldObjectServices.Current?.CurrentTimeSeconds ?? 0.0f;
        float elapsed = now - LastTickReplicationTime;
        if (elapsed < TimeBetweenReplications)
            return;

        LastTickReplicationTime = now;

        foreach (PropertyInfo property in replication.ReplicateOnTickProperties.Values)
            EnqueuePropertyReplication(property.Name, property.GetValue(this), replication.CompressedPropertyNames.Contains(property.Name), resendOnFailedAck: false);
    }

    private static bool IsSettable(object? value, PropertyInfo property)
    {
        return value is null
            ? property.PropertyType.IsClass && !property.PropertyType.IsValueType
            : property.PropertyType.IsAssignableFrom(value.GetType());
    }
}
