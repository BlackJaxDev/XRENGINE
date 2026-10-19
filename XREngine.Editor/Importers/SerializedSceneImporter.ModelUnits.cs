using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Fbx;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Importers;

internal static partial class SerializedSceneImporter
{
    /// <summary>
    /// Keeps file units in geometry and source translations before prefab overrides.
    /// A scale override must not replace the conversion from source units to metres.
    /// </summary>
    private static void RestoreSourceLocalModelUnits(SceneNode wrapper, string backend,
        bool sourceZUp, float sourceUnitToMeters, FbxSemanticDocument source)
    {
        bool native = backend == ModelImportBackendIds.NativeFbx;
        Matrix4x4 axisBasis = sourceZUp
            ? Matrix4x4.CreateRotationX(MathF.PI * 0.5f) : Matrix4x4.Identity;
        Matrix4x4 expectedBasis = native
            ? Matrix4x4.CreateScale(sourceUnitToMeters) * axisBasis : axisBasis;
        if (wrapper.Components.Count != 0 ||
            !ModelMatricesMatch(wrapper.Transform.LocalMatrix, Matrix4x4.Identity) ||
            wrapper.Transform.Children.Count != 1 ||
            wrapper.Transform.Children[0].SceneNode is not { Name: "RootNode" } syntheticRoot ||
            syntheticRoot.Transform is not Transform syntheticTransform ||
            syntheticRoot.Components.Count != 0 ||
            !ModelMatricesMatch(syntheticTransform.LocalMatrix, expectedBasis))
            throw new SourceVisualImportException("The centimetre model does not have the declared synthetic root basis.");

        IReadOnlyList<FbxIntermediateNode> sourceNodes = source.IntermediateScene.Nodes;
        var targets = new SceneNode[sourceNodes.Count];
        var oldWorlds = new Matrix4x4[sourceNodes.Count];
        var mapped = new HashSet<SceneNode>();
        int rootCount = 0;
        for (int index = 0; index < sourceNodes.Count; index++)
        {
            if (sourceNodes[index].ParentNodeIndex is not null)
                continue;
            MapSourceNode(index, syntheticRoot);
            rootCount++;
        }
        if (rootCount != syntheticTransform.Children.Count || mapped.Count != sourceNodes.Count)
            throw new SourceVisualImportException("The centimetre model hierarchy does not match the source hierarchy.");

        for (int index = 0; index < targets.Length; index++)
        {
            SceneNode node = targets[index];
            oldWorlds[index] = node.Transform.WorldMatrix;
            foreach (ModelComponent component in node.Components.OfType<ModelComponent>())
                if (component.Model is { } model)
                    foreach (SubMesh subMesh in model.Meshes)
                        foreach (SubMeshLOD lod in subMesh.LODs)
                            if (lod.Mesh is { } mesh && (mesh.HasSkinning || mesh.BindRootMatrix is not null))
                                throw new SourceVisualImportException(
                                    $"Centimetre mesh '{mesh.Name}' requires unsupported source-local bind conversion.");
        }

        var previousState = CaptureImportedRootState(syntheticRoot);
        Matrix4x4 chart = Matrix4x4.CreateScale(-1.0f, 1.0f, -1.0f) *
            Matrix4x4.CreateScale(sourceUnitToMeters);
        Matrix4x4 inverseChart = Matrix4x4.CreateScale(1.0f / sourceUnitToMeters) *
            Matrix4x4.CreateScale(-1.0f, 1.0f, -1.0f);
        for (int index = 0; index < targets.Length; index++)
        {
            Matrix4x4 local = inverseChart * sourceNodes[index].LocalTransform * chart;
            if (targets[index].Transform is not Transform transform ||
                !Matrix4x4.Decompose(local, out Vector3 scale, out Quaternion rotation, out Vector3 translation) ||
                !ModelMatricesMatch(local, Matrix4x4.CreateScale(scale) *
                    Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation)))
                throw new SourceVisualImportException($"Source node '{sourceNodes[index].Name}' has no lossless local TRS conversion.");
            transform.DeriveLocalMatrix(local);
        }
        syntheticTransform.DeriveLocalMatrix(axisBasis);
        wrapper.Transform.RecalculateMatrixHierarchy(true, true, ELoopType.Sequential).GetAwaiter().GetResult();

        var meshConversions = new Dictionary<XRMesh, Matrix4x4>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        for (int index = 0; index < targets.Length; index++)
        {
            SceneNode node = targets[index];
            if (!Matrix4x4.Invert(node.Transform.WorldMatrix, out Matrix4x4 inverseWorld) ||
                !ModelMatricesMatch(node.Transform.WorldMatrix * inverseWorld, Matrix4x4.Identity))
                throw new SourceVisualImportException($"Source node '{node.Name}' has a singular local-unit chart.");
            Matrix4x4 conversion = oldWorlds[index] * inverseWorld;
            foreach (ModelComponent component in node.Components.OfType<ModelComponent>())
                if (component.Model is { } model)
                    foreach (SubMesh subMesh in model.Meshes)
                        foreach (SubMeshLOD lod in subMesh.LODs)
                            if (lod.Mesh is { } mesh && !meshConversions.TryAdd(mesh, conversion) &&
                                !ModelMatricesMatch(meshConversions[mesh], conversion))
                                throw new SourceVisualImportException(
                                    $"Shared mesh '{mesh.Name}' requires different source-local unit conversions.");
        }
        foreach ((XRMesh mesh, Matrix4x4 conversion) in meshConversions)
        {
            AdvancedGpuDeformationResources.DiscardImportedMeshPreparation(mesh);
            mesh.TransformImportGeometry(conversion);
        }
        foreach (SceneNode node in targets)
            foreach (ModelComponent component in node.Components.OfType<ModelComponent>())
                if (component.Model is { } model)
                    foreach (SubMesh subMesh in model.Meshes)
                        subMesh.Bounds = subMesh.CalculateBoundingBox();
        SaveImportedModelBindHierarchy(wrapper.Transform);
        RestoreImportedRootState(previousState, null, null, syntheticTransform);

        void MapSourceNode(int index, SceneNode parent)
        {
            FbxIntermediateNode sourceNode = sourceNodes[index];
            SceneNode? target = null;
            foreach (TransformBase child in parent.Transform.Children)
            {
                if (child.SceneNode is not { } candidate || candidate.Name != sourceNode.Name)
                    continue;
                if (target is not null)
                    throw new SourceVisualImportException($"Source node '{sourceNode.Name}' has duplicate imported siblings.");
                target = candidate;
            }
            if (target is null || !mapped.Add(target) ||
                target.Transform.Children.Count != sourceNode.ChildNodeIndices.Count)
                throw new SourceVisualImportException($"Source node '{sourceNode.Name}' has no unique imported hierarchy match.");
            targets[index] = target;
            foreach (int child in sourceNode.ChildNodeIndices)
                MapSourceNode(child, target);
        }
    }
}
