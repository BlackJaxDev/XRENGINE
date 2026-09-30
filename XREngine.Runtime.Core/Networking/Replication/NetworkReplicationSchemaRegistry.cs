using System.Collections.Concurrent;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Networking;

/// <summary>Explicit, AOT-safe allowlist for replicated entity factories and game component codecs.</summary>
public static class NetworkReplicationSchemaRegistry
{
    public const int MaximumComponentBytes = 16 * 1024;
    private static readonly ConcurrentDictionary<string, NetworkComponentSchema> Components = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Func<SceneNode>> Factories = new(StringComparer.Ordinal);
    private static readonly object RegistryLock = new();
    private static string? _fingerprint;

    public static string Fingerprint { get { Freeze(); return _fingerprint!; } }

    /// <summary>Freezes registration for the process before validation or serialization begins.</summary>
    public static void Freeze()
    {
        lock (RegistryLock)
        {
            if (_fingerprint is not null)
                return;
            string manifest = string.Join('\n', Factories.Keys.Order(StringComparer.Ordinal).Select(static id => $"entity:{id}:1")
                .Concat(Components.Values.OrderBy(static schema => schema.Id, StringComparer.Ordinal)
                    .Select(static schema => $"component:{schema.Id}:{schema.Version}:{schema.ComponentType.FullName}")));
            _fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest)));
        }
    }

    static NetworkReplicationSchemaRegistry()
    {
        RegisterEntityFactory("scene-node-v1", static () => new SceneNode("ReplicatedEntity", new Transform()));
        RegisterComponent<ReplicatedStateComponent>("state-v1", 1,
            static node => node.AddComponent(static () => new ReplicatedStateComponent())!,
            static component => new ReplicatedComponentState
            {
                Payload = component.Payload.ToArray(),
                EntityReferences = component.EntityReferences.ToArray(),
                AssetReferences = component.AssetReferences.ToArray(),
            },
            static state => state.Payload.Length <= MaximumComponentBytes,
            static (component, state, _) =>
            {
                component.Payload = state.Payload.ToArray();
                component.EntityReferences = state.EntityReferences.ToArray();
                component.AssetReferences = state.AssetReferences.ToArray();
            });
    }

    public static void RegisterEntityFactory(string id, Func<SceneNode> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        lock (RegistryLock)
        {
            if (_fingerprint is not null)
                throw new InvalidOperationException("Replication schemas are frozen for this process.");
            if (id.Length > 128 || !Factories.TryAdd(id, factory))
                throw new InvalidOperationException($"Replication factory '{id}' is invalid or already registered.");
        }
    }

    public static void RegisterComponent<T>(string id, ushort version, Func<SceneNode, T> create,
        Func<T, ReplicatedComponentState> capture, Func<ReplicatedComponentState, bool> validate,
        Action<T, ReplicatedComponentState, IReadOnlyDictionary<NetworkEntityId, SceneNode>> apply) where T : XRComponent
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(apply);
        if (version == 0 || id.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(version));
        var schema = new NetworkComponentSchema(id, version, typeof(T), node => create(node), component => capture((T)component),
            validate, (component, state, entities) => apply((T)component, state, entities));
        lock (RegistryLock)
        {
            if (_fingerprint is not null)
                throw new InvalidOperationException("Replication schemas are frozen for this process.");
            if (Components.Values.Any(existing => existing.ComponentType == typeof(T)) || !Components.TryAdd(id, schema))
                throw new InvalidOperationException($"Replication component schema '{id}' or its component type is already registered.");
        }
    }

    public static bool TryGetComponent(string id, ushort version, out NetworkComponentSchema? schema)
        => Components.TryGetValue(id, out schema) && schema.Version == version;

    public static bool TryGetComponent(string id, out NetworkComponentSchema? schema) => Components.TryGetValue(id, out schema);
    public static bool SupportsFactory(string id, ushort version) => version == 1 && Factories.ContainsKey(id);

    public static SceneNode CreateEntity(string id, ushort version)
        => version == 1 && Factories.TryGetValue(id, out var factory)
            ? factory()
            : throw new InvalidOperationException($"Unsupported replication factory '{id}' version {version}.");
}
