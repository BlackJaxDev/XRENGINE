using XREngine.Components;

namespace XREngine.Networking;

/// <summary>Opt-in opaque game state with explicit entity and verified package asset references.</summary>
public sealed class ReplicatedStateComponent : XRComponent
{
    private byte[] _payload = [];
    private NetworkEntityId[] _entityReferences = [];
    private string[] _assetReferences = [];

    public byte[] Payload { get => _payload; set => SetField(ref _payload, value ?? []); }
    public NetworkEntityId[] EntityReferences { get => _entityReferences; set => SetField(ref _entityReferences, value ?? []); }
    public string[] AssetReferences { get => _assetReferences; set => SetField(ref _assetReferences, value ?? []); }
}
