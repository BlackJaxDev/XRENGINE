using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Importers;

internal static partial class UnitySceneImporter
{
    /// <summary>
    /// Applies the skeleton pose Unity persisted after importing the FBX. This reconciles
    /// Assimp FBX axis decomposition with the coordinate frame used by Unity prefab overrides.
    /// </summary>
    private static void ApplyUnityImportedSkeletonBindPose(
        SceneNode root,
        UnityModelImporterDocument metadata,
        ImportState state)
    {
        if (metadata.SkeletonTransforms.Count == 0)
            return;

        var resolvedBySkeletonName = new Dictionary<string, SceneNode>(StringComparer.Ordinal);
        int appliedCount = 0;
        int unresolvedCount = 0;

        for (int entryIndex = 0; entryIndex < metadata.SkeletonTransforms.Count; entryIndex++)
        {
            UnityModelSkeletonTransform entry = metadata.SkeletonTransforms[entryIndex];
            SceneNode? node = ResolveSkeletonNode(root, entry, resolvedBySkeletonName);
            if (node?.Transform is not Transform transform)
            {
                unresolvedCount++;
                continue;
            }

            transform.Translation = ConvertPosition(entry.Position);
            transform.Rotation = ConvertRotation(entry.Rotation);
            transform.Scale = entry.Scale;
            resolvedBySkeletonName.TryAdd(entry.Name, node);
            appliedCount++;
        }

        if (appliedCount == 0)
            return;

        root.Transform
            .RecalculateMatrixHierarchy(
                forceWorldRecalc: true,
                setRenderMatrixNow: true,
                childRecalcType: ELoopType.Sequential)
            .GetAwaiter()
            .GetResult();

        SceneNode[] hierarchy = [.. EnumerateModelHierarchy(root)];
        for (int nodeIndex = 0; nodeIndex < hierarchy.Length; nodeIndex++)
            hierarchy[nodeIndex].Transform.SaveBindState();

        var rebasedMeshes = new HashSet<XRMesh>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        for (int nodeIndex = 0; nodeIndex < hierarchy.Length; nodeIndex++)
        {
            foreach (ModelComponent component in hierarchy[nodeIndex].Components.OfType<ModelComponent>())
            {
                if (component.Model is not Model model)
                    continue;

                foreach (SubMesh subMesh in model.Meshes)
                {
                    foreach (SubMeshLOD lod in subMesh.LODs)
                    {
                        if (lod.Mesh is not { HasSkinning: true } mesh || !rebasedMeshes.Add(mesh))
                            continue;

                        mesh.RebaseSkinningBindPoseToCurrentHierarchy();
                    }
                }
            }
        }

        state.Context.AddDiagnostic(
            "UNITYMODEL0005",
            UnityImportDiagnosticSeverity.Info,
            UnityImportDiagnosticCategory.ModelIdentity,
            $"Applied Unity's imported skeleton bind pose to {appliedCount} transform(s), " +
            $"rebased {rebasedMeshes.Count} skinned mesh(es), and left {unresolvedCount} metadata transform(s) unresolved.",
            metadata.SourceMetaPath);
    }

    private static SceneNode? ResolveSkeletonNode(
        SceneNode root,
        UnityModelSkeletonTransform entry,
        IReadOnlyDictionary<string, SceneNode> resolvedBySkeletonName)
    {
        if (string.IsNullOrWhiteSpace(entry.ParentName))
            return root;

        if (resolvedBySkeletonName.TryGetValue(entry.ParentName, out SceneNode? resolvedParent))
        {
            SceneNode? match = null;
            foreach (TransformBase childTransform in resolvedParent.Transform.Children)
            {
                if (childTransform.SceneNode is not SceneNode child ||
                    !string.Equals(child.Name, entry.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (match is not null)
                    return null;
                match = child;
            }

            if (match is not null)
                return match;
        }

        SceneNode? unique = null;
        foreach (SceneNode candidate in EnumerateModelHierarchy(root))
        {
            if (!string.Equals(candidate.Name, entry.Name, StringComparison.Ordinal) ||
                !string.Equals(candidate.Parent?.Name, entry.ParentName, StringComparison.Ordinal))
            {
                continue;
            }

            if (unique is not null)
                return null;
            unique = candidate;
        }

        return unique;
    }

    private static IEnumerable<SceneNode> EnumerateModelHierarchy(SceneNode root)
    {
        var stack = new Stack<SceneNode>();
        stack.Push(root);
        while (stack.TryPop(out SceneNode? node))
        {
            yield return node;
            for (int childIndex = node.Transform.Children.Count - 1; childIndex >= 0; childIndex--)
            {
                if (node.Transform.Children[childIndex].SceneNode is SceneNode child)
                    stack.Push(child);
            }
        }
    }
}
