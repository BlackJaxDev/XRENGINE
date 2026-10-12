using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Importers;

internal static partial class SerializedSceneImporter
{
    /// <summary>
    /// Applies the skeleton pose Unity persisted after importing the FBX. This reconciles
    /// Assimp FBX axis decomposition with the coordinate frame used by Unity prefab overrides.
    /// </summary>
    private static void ApplySourceImportedSkeletonBindPose(
        SceneNode root,
        SerializedModelImporterDocument metadata,
        ImportState state)
    {
        if (metadata.SkeletonTransforms.Count == 0)
            return;

        SceneNode[] hierarchy = [.. EnumerateModelHierarchy(root)];
        var meshes = new HashSet<XRMesh>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        var previousBindMatrices = new Dictionary<TransformBase, Matrix4x4>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        var unskinnedPlacements = new List<(XRMesh Mesh, TransformBase Owner, Matrix4x4 World)>();
        var boundsPlacements = new List<(SubMesh SubMesh, TransformBase Basis, Matrix4x4 World, AABB Bounds)>();
        foreach (SceneNode node in hierarchy)
        {
            foreach (ModelComponent component in node.Components.OfType<ModelComponent>())
            {
                if (component.Model is not Model model)
                    continue;

                foreach (SubMesh subMesh in model.Meshes)
                {
                    if (subMesh.CullingBounds is { } bounds)
                    {
                        TransformBase basis = subMesh.RootBone ?? subMesh.RootTransform ?? node.Transform;
                        boundsPlacements.Add((subMesh, basis, basis.WorldMatrix, bounds));
                    }
                    foreach (SubMeshLOD lod in subMesh.LODs)
                    {
                        if (lod.Mesh is not { } mesh)
                            continue;
                        if (!mesh.HasSkinning)
                        {
                            unskinnedPlacements.Add((mesh, node.Transform, node.Transform.WorldMatrix));
                            continue;
                        }
                        if (!meshes.Add(mesh))
                            continue;

                        foreach ((TransformBase bone, _) in mesh.UtilizedBones)
                            previousBindMatrices.TryAdd(bone, bone.BindMatrix);
                    }
                }
            }
        }

        var resolvedBySkeletonName = new Dictionary<string, SceneNode>(StringComparer.Ordinal);
        int appliedCount = 0;
        int unresolvedCount = 0;

        for (int entryIndex = 0; entryIndex < metadata.SkeletonTransforms.Count; entryIndex++)
        {
            SerializedModelSkeletonTransform entry = metadata.SkeletonTransforms[entryIndex];
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

        for (int nodeIndex = 0; nodeIndex < hierarchy.Length; nodeIndex++)
            hierarchy[nodeIndex].Transform.SaveBindState();

        float maximumProductError = 0.0f;
        foreach (XRMesh mesh in meshes)
            maximumProductError = MathF.Max(maximumProductError,
                mesh.RebaseSkinningBindPoseToCurrentHierarchy(previousBindMatrices));

        var geometryConversions = new Dictionary<XRMesh, Matrix4x4>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach ((XRMesh mesh, TransformBase owner, Matrix4x4 oldWorld) in unskinnedPlacements)
        {
            Matrix4x4 conversion = oldWorld * InvertImportedBasis(owner.WorldMatrix, mesh.Name);
            if (geometryConversions.TryGetValue(mesh, out Matrix4x4 existing) &&
                !ModelMatricesMatch(existing, conversion))
                throw new SourceVisualImportException(
                    $"Shared mesh '{mesh.Name}' requires different geometry conversions after Unity skeleton import.");
            geometryConversions[mesh] = conversion;
        }
        foreach ((XRMesh mesh, Matrix4x4 conversion) in geometryConversions)
            if (!ModelMatricesMatch(conversion, Matrix4x4.Identity))
                mesh.TransformImportGeometry(conversion);

        foreach (SceneNode node in hierarchy)
            foreach (ModelComponent component in node.Components.OfType<ModelComponent>())
                if (component.Model is { } model)
                    foreach (SubMesh subMesh in model.Meshes)
                        subMesh.Bounds = subMesh.CalculateBoundingBox();

        foreach ((SubMesh subMesh, TransformBase basis, Matrix4x4 oldWorld, AABB bounds) in boundsPlacements)
            subMesh.CullingBounds = bounds.Transformed(oldWorld * InvertImportedBasis(basis.WorldMatrix, subMesh.Name));

        state.Context.AddDiagnostic(
            "UNITYMODEL0005",
            SourceImportDiagnosticSeverity.Info,
            SourceImportDiagnosticCategory.ModelIdentity,
            $"Applied Unity's imported skeleton bind pose to {appliedCount} transform(s), " +
            $"rebased {meshes.Count} skinned mesh(es) with maximum bind-product error {maximumProductError:R}, " +
            $"and left {unresolvedCount} metadata transform(s) unresolved.",
            metadata.SourceMetaPath);
    }

    private static Matrix4x4 InvertImportedBasis(Matrix4x4 basis, string? name)
    {
        if (!Matrix4x4.Invert(basis, out Matrix4x4 inverse) ||
            !ModelMatricesMatch(basis * inverse, Matrix4x4.Identity))
            throw new SourceVisualImportException($"Imported object '{name}' has a singular or non-finite basis.");
        return inverse;
    }

    private static SceneNode? ResolveSkeletonNode(
        SceneNode root,
        SerializedModelSkeletonTransform entry,
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
