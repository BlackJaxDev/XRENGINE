using System.Numerics;
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
/// The T0 size of a sub-mesh is the lowest LOD: <see cref="MeshProxyBytesPerVertex"/> bytes for each vertex,
/// which is 64 bytes of vertex data and about five 16-bit indices.
/// </remarks>
public sealed class CutsceneRenderableAssetCollector : ICutsceneResidencyAssetCollector
{
    /// <summary>The estimated T0 size of one texture: 64 x 64 texels x 4 bytes x 4/3 for the mip chain.</summary>
    public const long TextureProxyBytes = 64L * 64L * 4L * 4L / 3L;

    /// <summary>The estimated T0 size of one vertex of the lowest mesh LOD, with its share of the indices.</summary>
    public const long MeshProxyBytesPerVertex = 64L + (5L * 2L);

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

        SubMeshLOD? lowest = null;
        foreach (SubMeshLOD lod in subMesh.LODs)
        {
            lowest = lod;
            if (lod.Material is not { } material)
                continue;

            var textures = material.Textures;
            for (int t = 0; t < textures.Count; t++)
            {
                if (textures[t] is not XRTexture2D texture || !HasFileSource(texture))
                    continue;

                Guid key = CutsceneResidencyAssetKeys.ForTexture(texture);
                if (key != Guid.Empty)
                    destination.Add(new CutsceneResidencyAsset(key, EResidencyAssetKind.Texture, center, radius, TextureProxyBytes));
            }
        }

        // The LODs sort by visible distance, so the last LOD is the lowest one. It is the T0 data of the sub-mesh.
        long vertices = lowest?.Mesh?.VertexCount ?? 0;
        destination.Add(new CutsceneResidencyAsset(subMeshKey, EResidencyAssetKind.Mesh, center, radius, vertices * MeshProxyBytesPerVertex));
    }

    /// <summary>
    /// True if the texture comes from a file, so that streaming can load its mips. A texture with no file, for example
    /// a generated default texture of a material, has all of its data in memory. Streaming cannot track it, so a
    /// residency request for it is always rejected.
    /// </summary>
    private static bool HasFileSource(XRTexture2D texture)
        => !string.IsNullOrWhiteSpace(texture.FilePath) || !string.IsNullOrWhiteSpace(texture.OriginalPath);
}
