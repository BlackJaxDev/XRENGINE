using XREngine.Components.Scene.Mesh;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Scene;

namespace XREngine.Browser;

public static partial class BrowserEngineExports
{
    /// <summary>Requires an explicit verified kernel before an authored deforming mesh enters the browser world.</summary>
    private static void AdmitMeshDeformation(XRWorld world, WebComputeArtifactCatalog artifacts)
    {
        if (artifacts.TryResolve(WebComputeArtifactCatalog.PackedSkinningKernel, out _))
            return;
        HashSet<SceneNode> visited = new(ReferenceEqualityComparer.Instance);
        foreach (XRScene scene in world.Scenes)
            foreach (SceneNode root in scene.RootNodes)
                Visit(root, 0);

        void Visit(SceneNode node, int depth)
        {
            if (depth >= 128 || !visited.Add(node) || visited.Count > 8192)
                throw new NotSupportedException("AssetSource.SceneGraphUnsupported: deformation admission requires a bounded tree of distinct scene nodes.");
            foreach (var component in node.Components)
            {
                if (component is not ModelComponent { Model: { } model })
                    continue;
                foreach (var mesh in model.Meshes)
                    foreach (var lod in mesh.LODs)
                        if (lod.Mesh is { } geometry && (geometry.HasSkinning || geometry.HasBlendshapes))
                            throw new NotSupportedException($"AssetSource.ComputeArtifactMissing: '{node.GetPath()}' mesh '{mesh.Name}' requires packed-skinning.");
            }
            foreach (var child in node.Transform.Children)
                if (child.SceneNode is SceneNode childNode)
                    Visit(childNode, depth + 1);
        }
    }
}
