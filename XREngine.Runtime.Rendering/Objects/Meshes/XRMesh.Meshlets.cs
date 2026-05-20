using System.ComponentModel;
using MemoryPack;
using XREngine.Rendering.Meshlets;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class XRMesh
{
    private MeshletPayload? _meshletPayload;

    [Browsable(false)]
    [MemoryPackIgnore]
    [YamlIgnore]
    public MeshletPayload? MeshletPayload
    {
        get => _meshletPayload;
        set => SetField(ref _meshletPayload, value);
    }

    [Browsable(false)]
    [MemoryPackIgnore]
    [YamlIgnore]
    public bool HasMeshletPayload
        => _meshletPayload is not null;

    public MeshletPayload GetOrCreateMeshletPayload(
        MeshletGenerationSettings? meshletSettings,
        MeshLodGenerationSettings? lodSettings = null,
        string? sourceMeshIdentity = null)
    {
        meshletSettings ??= new MeshletGenerationSettings
        {
            Enabled = true,
            BuildMode = MeshletBuildMode.Dense,
        };

        if (_meshletPayload is { } payload &&
            payload.IsFreshFor(this, meshletSettings, lodSettings, sourceMeshIdentity))
        {
            return payload;
        }

        MeshletPayload newPayload = meshletSettings.Enabled
            ? MeshOptimizerIntegration.BuildMeshlets(this, meshletSettings, lodSettings, sourceMeshIdentity).Payload
            : MeshletPayload.CreateDisabled(this, meshletSettings, lodSettings, sourceMeshIdentity);

        MeshletPayload = newPayload;
        return newPayload;
    }

    public bool TryGetFreshMeshletPayload(
        MeshletGenerationSettings? meshletSettings,
        MeshLodGenerationSettings? lodSettings,
        string? sourceMeshIdentity,
        out MeshletPayload payload)
    {
        if (_meshletPayload is { } existing &&
            existing.IsFreshFor(this, meshletSettings, lodSettings, sourceMeshIdentity))
        {
            payload = existing;
            return true;
        }

        payload = null!;
        return false;
    }
}
