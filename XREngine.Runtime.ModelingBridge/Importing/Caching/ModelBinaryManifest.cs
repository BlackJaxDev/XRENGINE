using System.Collections.ObjectModel;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Lightweight semantic manifest read before any heavy model payload.
/// </summary>
internal sealed class ModelBinaryManifest
{
    private readonly ReadOnlyCollection<ModelBinaryManifestCandidate> _candidates;

    public ModelBinaryManifest(
        ulong featureFlags,
        string actualProducerId,
        string sourceExtension,
        uint resolverPolicyVersion,
        ModelImportBackendPolicy requestedPolicy,
        ModelImportBackendPolicy hostPreference,
        IEnumerable<ModelBinaryManifestCandidate> candidates,
        uint sourceEntityCount = 0,
        uint referenceCount = 0,
        ulong nodeCount = 0,
        ulong modelCount = 0,
        ulong subMeshCount = 0,
        ulong meshCount = 0,
        ulong vertexCount = 0,
        ulong indexCount = 0,
        ulong boneCount = 0,
        ulong morphTargetCount = 0,
        ulong lodCount = 0,
        ulong meshletCount = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actualProducerId);
        ArgumentNullException.ThrowIfNull(candidates);

        ModelBinaryManifestCandidate[] candidateArray = candidates.ToArray();
        if (candidateArray.Length == 0)
            throw new ArgumentException("A model-cache manifest must record at least one backend candidate.", nameof(candidates));
        if (!candidateArray.Any(candidate =>
            candidate.StableId.Equals(actualProducerId, StringComparison.Ordinal)))
            throw new ArgumentException("The actual producer must belong to the resolver candidate list.", nameof(actualProducerId));

        FeatureFlags = featureFlags;
        ActualProducerId = actualProducerId;
        SourceExtension = sourceExtension ?? string.Empty;
        ResolverPolicyVersion = resolverPolicyVersion;
        RequestedPolicy = requestedPolicy;
        HostPreference = hostPreference;
        _candidates = Array.AsReadOnly(candidateArray);
        SourceEntityCount = sourceEntityCount;
        ReferenceCount = referenceCount;
        NodeCount = nodeCount;
        ModelCount = modelCount;
        SubMeshCount = subMeshCount;
        MeshCount = meshCount;
        VertexCount = vertexCount;
        IndexCount = indexCount;
        BoneCount = boneCount;
        MorphTargetCount = morphTargetCount;
        LodCount = lodCount;
        MeshletCount = meshletCount;
    }

    public ulong FeatureFlags { get; }
    public string ActualProducerId { get; }
    public string SourceExtension { get; }
    public uint ResolverPolicyVersion { get; }
    public ModelImportBackendPolicy RequestedPolicy { get; }
    public ModelImportBackendPolicy HostPreference { get; }
    public IReadOnlyList<ModelBinaryManifestCandidate> Candidates => _candidates;
    public uint SourceEntityCount { get; }
    public uint ReferenceCount { get; }
    public ulong NodeCount { get; }
    public ulong ModelCount { get; }
    public ulong SubMeshCount { get; }
    public ulong MeshCount { get; }
    public ulong VertexCount { get; }
    public ulong IndexCount { get; }
    public ulong BoneCount { get; }
    public ulong MorphTargetCount { get; }
    public ulong LodCount { get; }
    public ulong MeshletCount { get; }
}
