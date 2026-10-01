using System.Numerics;
using XREngine.Components;
using XREngine.Components.Physics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene.Physics;

namespace XREngine;

/// <summary>
/// Adapts facade-owned model and render meshes to Runtime.Core collision input.
/// </summary>
internal sealed class EngineConvexHullInputProvider : IConvexHullInputProvider
{
    public bool TryCollect(
        XRComponent component,
        out ConvexHullInputCollection inputs,
        out string targetLabel)
    {
        List<ModelComponent> models = ResolveModels(component);
        targetLabel = DescribeTarget(component, models);
        if (models.Count == 0)
        {
            inputs = default;
            return false;
        }

        inputs = Collect(models, component.Transform);
        return inputs.Runtime.InputCount > 0 || inputs.Asset.InputCount > 0;
    }

    private static List<ModelComponent> ResolveModels(XRComponent component)
    {
        if (component is ModelComponent modelComponent)
            return [modelComponent];

        if (component is StaticRigidBodyComponent staticBody)
        {
            List<ModelComponent> targets = [];
            if (staticBody.TargetModelComponents is { Count: > 0 } configuredTargets)
            {
                for (int i = 0; i < configuredTargets.Count; i++)
                    if (configuredTargets[i] is ModelComponent model)
                        targets.Add(model);
                return targets;
            }

            if (staticBody.TargetModelComponent is ModelComponent configuredTarget)
                targets.Add(configuredTarget);
            else if (staticBody.GetSiblingComponent<ModelComponent>() is { } sibling)
                targets.Add(sibling);
            return targets;
        }

        return component.GetSiblingComponent<ModelComponent>() is { } siblingModel
            ? [siblingModel]
            : [];
    }

    private static ConvexHullInputCollection Collect(
        IReadOnlyList<ModelComponent> components,
        Scene.Transforms.TransformBase targetTransform)
    {
        List<ConvexHullInput> runtimeInputs = [];
        List<ConvexHullInput> assetInputs = [];
        int runtimeMeshCount = 0;
        int assetMeshCount = 0;

        for (int i = 0; i < components.Count; i++)
        {
            ModelComponent component = components[i];
            Matrix4x4 localToTarget = component.Transform.WorldMatrix * targetTransform.InverseWorldMatrix;
            Matrix4x4? transform = localToTarget.IsIdentity ? null : localToTarget;

            RenderableMesh[] runtimeMeshes = [.. component.Meshes.ToArray()];
            runtimeMeshCount += runtimeMeshes.Length;
            CollectRuntimeMeshes(runtimeMeshes, transform, runtimeInputs);

            Model? model = component.Model;
            if (model is null)
                continue;

            assetMeshCount += model.Meshes.Count;
            CollectAssetMeshes(model, transform, assetInputs);
        }

        return new ConvexHullInputCollection(
            new ConvexHullInputBatch(ConvexHullInputSource.RuntimeMeshes, runtimeInputs, runtimeMeshCount),
            new ConvexHullInputBatch(ConvexHullInputSource.AssetMeshes, assetInputs, assetMeshCount));
    }

    private static void CollectRuntimeMeshes(
        IReadOnlyList<RenderableMesh> renderables,
        Matrix4x4? transform,
        List<ConvexHullInput> inputs)
    {
        for (int i = 0; i < renderables.Count; i++)
        {
            XRMesh? mesh = renderables[i].CurrentLODMesh;
            if (mesh is null)
            {
                foreach (RenderableMesh.RenderableLOD lod in renderables[i].GetLodSnapshot())
                {
                    if (lod.Renderer?.Mesh is not XRMesh candidate)
                        continue;
                    mesh = candidate;
                    break;
                }
            }

            TryAdd(mesh, transform, inputs);
        }
    }

    private static void CollectAssetMeshes(Model model, Matrix4x4? transform, List<ConvexHullInput> inputs)
    {
        foreach (SubMesh subMesh in model.Meshes)
        {
            XRMesh? mesh = null;
            foreach (SubMeshLOD lod in subMesh.LODs)
            {
                if (lod.Mesh is not XRMesh candidate)
                    continue;
                mesh = candidate;
                break;
            }

            TryAdd(mesh, transform, inputs);
        }
    }

    private static void TryAdd(XRMesh? mesh, Matrix4x4? transform, List<ConvexHullInput> inputs)
    {
        if (mesh?.Vertices is not { Length: > 0 } vertices)
            return;

        int[]? indices = mesh.GetIndices(EPrimitiveType.Triangles);
        if (indices is null)
            return;

        Vector3[] positions = new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            positions[i] = vertices[i].Position;

        if (ConvexHullUtility.TryCreateInput(positions, indices, transform, out ConvexHullInput input))
            inputs.Add(input);
    }

    private static string DescribeTarget(XRComponent component, IReadOnlyList<ModelComponent> models)
    {
        string nodeLabel = component.SceneNode?.Name ?? "<unnamed>";
        if (models.Count != 1)
            return $"{nodeLabel} ({models.Count} model components)";

        ModelComponent model = models[0];
        string componentLabel = string.IsNullOrWhiteSpace(model.Name) ? model.GetType().Name : model.Name;
        return $"{nodeLabel}/{componentLabel}#{model.GetHashCode():X8}";
    }
}
