using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Extensions;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// Finds the textures and sub-meshes that the <see cref="ModelComponent"/> instances of a node draw.
/// Each sub-mesh gives one mesh asset and one texture asset for each 2D texture of its LOD materials that comes from a file.
/// All of them use the world bounding sphere of the sub-mesh. The asset IDs are the stable keys of
/// <see cref="CutsceneResidencyAssetKeys"/>, not the runtime IDs, because a model import makes new runtime IDs.
/// </summary>
/// <remarks>
/// The T0 size of a texture is a 64 px preview with 4 bytes for each texel and a full mip chain:
/// <see cref="TextureProxyBytes"/>. The value is uncompressed, because compressed uploads are not implemented.
/// The T0 size of a sub-mesh is the size of the payload of its lowest LOD
/// (<see cref="SubMeshCookedCodec.GetCoarsestLodPayloadLength"/>): the payload length of the cooked LOD descriptor, or for
/// an editor sub-mesh the length that a cook would write for that mesh. A fixed size for each vertex is wrong for avatars,
/// whose blendshape and skinning data make a vertex much larger.
/// </remarks>
public sealed class CutsceneRenderableAssetCollector : ICutsceneResidencyAssetCollector
{
    /// <summary>The estimated T0 size of one texture: 64 x 64 texels x 4 bytes x 4/3 for the mip chain.</summary>
    public const long TextureProxyBytes = 64L * 64L * 4L * 4L / 3L;

    /// <summary>The shared instance. The collector has no state.</summary>
    public static CutsceneRenderableAssetCollector Instance { get; } = new();

    /// <inheritdoc/>
    public void Collect(SceneNode node, List<CutsceneResidencyAsset> destination)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(destination);

        var components = node.Components;
        for (int c = 0; c < components.Count; c++)
        {
            if (components[c] is not ModelComponent { Model: { } model })
                continue;

            Matrix4x4 world = node.Transform.WorldMatrix;
            Vector3 scale = world.ExtractScale();
            float maxScale = MathF.Max(MathF.Abs(scale.X), MathF.Max(MathF.Abs(scale.Y), MathF.Abs(scale.Z)));
            var meshes = model.Meshes;
            for (int m = 0; m < meshes.Count; m++)
                if (meshes[m] is { } subMesh)
                    CollectSubMesh(subMesh, CutsceneResidencyAssetKeys.ForSubMesh(node, c, m), world, maxScale, destination);
        }
    }

    private static void CollectSubMesh(SubMesh subMesh, Guid subMeshKey, Matrix4x4 world, float maxScale, List<CutsceneResidencyAsset> destination)
    {
        AABB bounds = subMesh.Bounds;
        Vector3 center = Vector3.Transform(bounds.Center, world);
        float radius = bounds.HalfExtents.Length() * maxScale;

        foreach (SubMeshLOD lod in subMesh.LODs)
        {
            if (lod.Material is not { } material)
                continue;

            var textures = material.Textures;
            for (int t = 0; t < textures.Count; t++)
            {
                if (textures[t] is not XRTexture2D texture || !HasFileSource(texture))
                    continue;

                Guid key = CutsceneResidencyAssetKeys.ForTexture(texture, out bool machineDependent);
                if (key != Guid.Empty)
                    destination.Add(new CutsceneResidencyAsset(key, EResidencyAssetKind.Texture, center, radius, TextureProxyBytes, machineDependent));
            }
        }

        // The LODs sort by visible distance, so the last LOD is the lowest one. It is the T0 data of the sub-mesh.
        destination.Add(new CutsceneResidencyAsset(subMeshKey, EResidencyAssetKind.Mesh, center, radius, GetProxyBytes(subMesh)));
    }

    /// <summary>
    /// Gets the T0 size of a sub-mesh. The size of an editor mesh needs a cook size pass, so the result is kept for each
    /// sub-mesh and is computed again only when the coarsest LOD mesh or its counts change. A mesh that cannot be sized
    /// (for example one with no CPU data) counts as zero bytes and gives one warning.
    /// </summary>
    private static long GetProxyBytes(SubMesh subMesh)
    {
        SubMeshLOD? coarsest = null;
        foreach (SubMeshLOD lod in subMesh.LODs)
            coarsest = lod;
        if (coarsest is null)
            return 0L;
        if (coarsest.CookedDescriptor is not null || coarsest.Mesh is not { } mesh)
            return SubMeshCookedCodec.GetCoarsestLodPayloadLength(subMesh);

        if (ProxySizes.TryGetValue(subMesh, out ProxySize? cached)
            && ReferenceEquals(cached.Mesh, mesh)
            && cached.VertexCount == mesh.VertexCount
            && cached.IndexCount == mesh.IndexCount
            && cached.BlendshapeCount == mesh.BlendshapeCount)
            return cached.Bytes;

        long bytes;
        try
        {
            bytes = SubMeshCookedCodec.GetCoarsestLodPayloadLength(subMesh);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            bytes = 0L;
            Debug.LogWarning(ELogCategory.Animation, "Cutscene residency: the T0 size of sub-mesh '{0}' is not known, so it counts as zero bytes: {1}", subMesh.Name ?? string.Empty, ex.Message);
        }
        ProxySizes.AddOrUpdate(subMesh, new ProxySize(mesh, mesh.VertexCount, mesh.IndexCount, mesh.BlendshapeCount, bytes));
        return bytes;
    }

    private static readonly ConditionalWeakTable<SubMesh, ProxySize> ProxySizes = new();

    /// <summary>The kept T0 size of a sub-mesh, with the values that make it stale.</summary>
    private sealed record ProxySize(XRMesh Mesh, int VertexCount, int IndexCount, uint BlendshapeCount, long Bytes);

    /// <summary>
    /// True if the texture comes from a file, so that streaming can load its mips. A texture with no file, for example
    /// a generated default texture of a material, has all of its data in memory. Streaming cannot track it, so a
    /// residency request for it is always rejected.
    /// </summary>
    private static bool HasFileSource(XRTexture2D texture)
        => !string.IsNullOrWhiteSpace(texture.FilePath) || !string.IsNullOrWhiteSpace(texture.OriginalPath);
}
