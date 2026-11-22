using System.ComponentModel;
using MemoryPack;
using XREngine.Data.Rendering;
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

    /// <summary>
    /// Attaches a cooked payload only after it has been validated for this
    /// owner. This is the sole warm-hydration accounting boundary.
    /// </summary>
    public void AttachValidatedCookedMeshletPayload(MeshletPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        payload.ValidateForMesh(this, payload.SourceMeshIdentity);
        MeshletPayload = payload;
        RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordMeshletWarmPayloadHydration();
    }

    /// <summary>
    /// Attaches a copy of the meshlet payload of <paramref name="source"/> to this mesh when this mesh
    /// uses the same geometry objects as the source. The runtime transform rebind clone is such a
    /// mesh: it uses the source buffers and topology lists and changes only bone references. Without
    /// this copy, the clone has no meshlets, so the meshlet and cluster level-of-detail routes skip it.
    /// </summary>
    /// <param name="source">The mesh whose geometry objects this mesh uses.</param>
    /// <returns>
    /// True when the copy is attached. False when the source has no payload that is valid for it, or
    /// when this mesh does not use the same geometry objects.
    /// </returns>
    internal bool TryAttachSharedGeometryMeshletPayload(XRMesh source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.MeshletPayload is not { } payload || !payload.IsValidatedFor(source))
            return false;
        if (!UsesSameGeometryAs(source))
            return false;

        MeshletPayload = payload.CreateCopyForSharedGeometryOwner(source, this);
        return true;
    }

    /// <summary>
    /// Checks that this mesh and <paramref name="source"/> use the same geometry objects: the same vertex
    /// count and primitive type, the same triangle list, and the same position, normal, tangent and
    /// interleaved vertex buffers. A meshlet payload that is valid for the source is then valid for
    /// this mesh, because it describes exactly these objects.
    /// </summary>
    private bool UsesSameGeometryAs(XRMesh source)
        => VertexCount == source.VertexCount
           && Type == source.Type
           && ReferenceEquals(_triangles, source._triangles)
           && UsesSameBuffer(source, ECommonBufferType.Position)
           && UsesSameBuffer(source, ECommonBufferType.Normal)
           && UsesSameBuffer(source, ECommonBufferType.Tangent)
           && UsesSameBuffer(source, ECommonBufferType.InterleavedVertex);

    private bool UsesSameBuffer(XRMesh source, ECommonBufferType bufferType)
    {
        string key = bufferType.ToString();
        return ReferenceEquals(Buffers.GetValueOrDefault(key), source.Buffers.GetValueOrDefault(key));
    }

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
            ? MeshOptimizerIntegration.BuildMeshletPayloadForMesh(this, meshletSettings, lodSettings, sourceMeshIdentity).Payload
            : MeshletPayload.CreateDisabled(this, meshletSettings, lodSettings, sourceMeshIdentity);

        newPayload.ValidateForMesh(this, sourceMeshIdentity);

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
