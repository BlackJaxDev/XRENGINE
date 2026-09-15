using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Networking;

/// <summary>A statically rooted component codec. Validation must be side-effect free; apply must be deterministic.</summary>
public sealed record NetworkComponentSchema(
    string Id,
    ushort Version,
    Type ComponentType,
    Func<SceneNode, XRComponent> Create,
    Func<XRComponent, ReplicatedComponentState> Capture,
    Func<ReplicatedComponentState, bool> Validate,
    Action<XRComponent, ReplicatedComponentState, IReadOnlyDictionary<NetworkEntityId, SceneNode>> Apply);
