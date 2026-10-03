using XREngine.Components;

namespace XREngine.Networking;

/// <summary>Opts a scene node into managed world replication. Game components require registered schemas.</summary>
public sealed class NetworkReplicatedComponent : XRComponent
{
    private string _factoryId = "scene-node-v1";
    private string[] _componentSchemas = [];
    private NetworkRelevanceHint? _relevance;

    public string FactoryId { get => _factoryId; set => SetField(ref _factoryId, value); }
    public string[] ComponentSchemas { get => _componentSchemas; set => SetField(ref _componentSchemas, value ?? []); }
    public NetworkRelevanceHint? Relevance { get => _relevance; set => SetField(ref _relevance, value); }
}
