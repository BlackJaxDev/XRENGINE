using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Fbx;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Importers;

internal static partial class SerializedSceneImporter
{
    /// <summary>
    /// Checks the source coordinate contract before Unity model composition.
    /// Unsupported source settings must not produce an approximate hierarchy.
    /// </summary>
    private static bool? ValidateSourceModelCoordinates(
        string modelPath, SerializedModelImporterDocument metadata, ImportState state)
    {
        if (!string.Equals(Path.GetExtension(modelPath), ".fbx", StringComparison.OrdinalIgnoreCase))
            return null;
        if (metadata.BakeAxisConversion || metadata.GlobalScale != 1.0f)
            throw new SourceVisualImportException(
                "Unity FBX composition currently requires bakeAxisConversion=false and globalScale=1.");

        // Check source semantics before either geometry producer runs.
        FbxSemanticDocument source = state.Context.FbxSourceDocuments.GetSemantic(modelPath);
        FbxAxisSystem? axes = source.GlobalSettings?.AxisSystem;
        if (axes is null || axes.UnitScaleFactor != 100.0d ||
            axes.CoordAxis.AxisIndex != 0 || axes.CoordAxis.Sign != 1 ||
            !((axes.UpAxis.AxisIndex == 2 && axes.UpAxis.Sign == 1 &&
               axes.FrontAxis.AxisIndex == 1 && axes.FrontAxis.Sign == -1) ||
              (axes.UpAxis.AxisIndex == 1 && axes.UpAxis.Sign == 1 &&
               axes.FrontAxis.AxisIndex == 2 && axes.FrontAxis.Sign == 1)))
            throw new SourceVisualImportException(
                "Unity FBX composition requires metre units and a supported +X-right source basis: " +
                "+Z-up/-Y-front or +Y-up/+Z-front.");

        foreach (FbxIntermediateNode node in source.IntermediateScene.Nodes)
        {
            FbxTransformSemantics transform = node.TransformSemantics;
            if (transform.HasPivotData || transform.InheritType != 1 ||
                transform.RotationOrder != FbxRotationOrder.XYZ ||
                !ModelMatricesMatch(node.GeometryTransform, Matrix4x4.Identity))
                throw new SourceVisualImportException(
                    $"Unity FBX node '{node.Name}' requires unsupported pivot, inheritance, rotation-order, or geometric-transform conversion.");
        }

        return axes.UpAxis.AxisIndex == 2;
    }

    /// <summary>
    /// Converts the producer's complete coordinate chart to the Unity YAML chart.
    /// Row-vector geometry, local transforms, and inverse binds change together.
    /// </summary>
    private static SceneNode ConvertImportedModelCoordinates(SceneNode root, string backend, bool nativeHasContentBasis)
    {
        bool native = backend == ModelImportBackendIds.NativeFbx;
        Matrix4x4 conversion = native
            ? Matrix4x4.CreateScale(-1.0f, 1.0f, -1.0f)
            : Matrix4x4.CreateScale(-1.0f, 1.0f, 1.0f);
        Matrix4x4 inverse = conversion;
        var meshes = new HashSet<XRMesh>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (SceneNode node in EnumerateModelHierarchy(root))
        {
            // Source clips are rebuilt after model normalization and identity assignment.
            while (node.GetComponent<AnimationClipComponent>() is not null)
                node.RemoveComponent<AnimationClipComponent>();
            if (node.Transform is not Transform transform)
                throw new SourceVisualImportException($"Imported node '{node.Name}' does not have a convertible TRS transform.");
            transform.DeriveLocalMatrix(inverse * transform.LocalMatrix * conversion);
            foreach (ModelComponent component in node.Components.OfType<ModelComponent>())
            {
                if (component.Model is not Model model)
                    continue;
                foreach (SubMesh subMesh in model.Meshes)
                {
                    if (subMesh.CullingBounds is { } bounds)
                        subMesh.CullingBounds = bounds.Transformed(conversion);
                    foreach (SubMeshLOD lod in subMesh.LODs)
                    {
                        if (lod.Mesh is not { } mesh || !meshes.Add(mesh))
                            continue;
                        // Coordinate conversion makes the producer's deformation input stale.
                        AdvancedGpuDeformationResources.DiscardImportedMeshPreparation(mesh);
                        mesh.TransformImportGeometry(conversion);
                        if (mesh.BindRootMatrix is { } bindRoot)
                            mesh.BindRootMatrix = inverse * bindRoot * conversion;
                        if (!mesh.HasSkinning)
                            continue;
                        var bindings = new (TransformBase tfm, Matrix4x4 invBindWorldMtx)[mesh.UtilizedBones.Length];
                        for (int index = 0; index < bindings.Length; index++)
                        {
                            var binding = mesh.UtilizedBones[index];
                            bindings[index] = (binding.tfm, inverse * binding.invBindWorldMtx * conversion);
                        }
                        mesh.ReplaceSkinningBoneBindings(bindings);
                    }
                    subMesh.Bounds = subMesh.CalculateBoundingBox();
                }
            }
        }
        root.Transform.RecalculateMatrixHierarchy(true, true, ELoopType.Sequential).GetAwaiter().GetResult();
        SaveImportedModelBindHierarchy(root.Transform);

        // The native content node has the same role as Assimp's generated root.
        // Use one normalization path before generation-2 Unity identities are assigned.
        if (native && nativeHasContentBasis)
        {
            if (root.Transform.Children.Count != 1 ||
                root.Transform.Children[0].SceneNode is not { Name: "FbxImportedContentBasis" } contentRoot ||
                !ModelMatricesMatch(contentRoot.Transform.LocalMatrix, Matrix4x4.CreateRotationX(MathF.PI * 0.5f)))
                throw new SourceVisualImportException("The native FBX producer did not return the declared content basis.");
            contentRoot.Name = "RootNode";
        }
        else if (native)
        {
            var generatedRoot = new SceneNode("RootNode", new Transform());
            foreach (TransformBase child in root.Transform.Children.ToArray())
                if (child.SceneNode is { } childNode)
                    childNode.Parent = generatedRoot;
            generatedRoot.Parent = root;
        }
        return root;
    }
}
