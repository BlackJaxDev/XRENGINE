using System.Text;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene;
using XREngine.Scene.Prefabs;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Reads authored submesh cook overrides from an existing project prefab without
/// consulting or parsing the original model source.
/// </summary>
internal static class ModelCookOverrideSnapshotBuilder
{
    public static ModelCookOverrideSnapshot Build(
        XRPrefabSource? projectPrefab,
        ModelCookSettings modelDefaults)
    {
        ArgumentNullException.ThrowIfNull(modelDefaults);
        if (projectPrefab?.RootNode is not SceneNode rootNode)
            return ModelCookOverrideSnapshot.Empty;

        byte[] defaultPolicy = ModelCookCanonicalSettings.SerializeSubMeshPolicy(
            modelDefaults.Meshlets,
            modelDefaults.Lods);
        List<ModelCookOverrideEntry> entries = [];
        AddNodeOverrides(rootNode, parentPath: "fallback", siblingIndex: 0, defaultPolicy, entries);
        return entries.Count == 0
            ? ModelCookOverrideSnapshot.Empty
            : new ModelCookOverrideSnapshot(entries);
    }

    private static void AddNodeOverrides(
        SceneNode node,
        string parentPath,
        int siblingIndex,
        ReadOnlySpan<byte> defaultPolicy,
        ICollection<ModelCookOverrideEntry> entries)
    {
        string nodeName = string.IsNullOrWhiteSpace(node.Name) ? "Node" : node.Name;
        string nodePath = $"{parentPath}/{EscapeKeySegment(nodeName)}[{siblingIndex}]";
        ModelComponent[] modelComponents = node.GetComponents<ModelComponent>().ToArray();
        for (int modelIndex = 0; modelIndex < modelComponents.Length; modelIndex++)
        {
            Model? model = modelComponents[modelIndex].Model;
            if (model is null)
                continue;

            for (int subMeshIndex = 0; subMeshIndex < model.Meshes.Count; subMeshIndex++)
            {
                SubMesh subMesh = model.Meshes[subMeshIndex];
                byte[] authoredPolicy = ModelCookCanonicalSettings.Serialize(subMesh.MeshOptimizer);
                if (authoredPolicy.AsSpan().SequenceEqual(defaultPolicy))
                    continue;

                ImportedEntityKey entityKey = new(
                    $"{nodePath}:model:{modelIndex}:submesh:{subMeshIndex}",
                    isStable: false);
                entries.Add(new ModelCookOverrideEntry(entityKey, subMesh.MeshOptimizer));
            }
        }

        for (int childIndex = 0; childIndex < node.Transform.Children.Count; childIndex++)
        {
            if (node.Transform.Children[childIndex]?.SceneNode is SceneNode childNode)
                AddNodeOverrides(childNode, nodePath, childIndex, defaultPolicy, entries);
        }
    }

    private static string EscapeKeySegment(string value)
        => value.Normalize(NormalizationForm.FormC)
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("/", "%2F", StringComparison.Ordinal);
}
