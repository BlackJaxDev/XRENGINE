using System.Numerics;
using System.Runtime.CompilerServices;
using MagicPhysX;
using XREngine.Components.Physics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Tools;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene.Physics.Physx;

namespace XREngine;

internal sealed class EngineRuntimeStaticColliderAuthoringServices : IRuntimeStaticColliderAuthoringServices
{
    private sealed class State
    {
        public bool Queued;
        public bool Started;
    }

    private readonly ConditionalWeakTable<StaticRigidBodyComponent, State> _states = new();

    public void OnActivated(StaticRigidBodyComponent component)
    {
        if (!component.AutoGenerateConvexCollidersFromSiblingModel)
            return;

        State state = _states.GetOrCreateValue(component);
        if (state.Queued || state.Started)
            return;

        state.Queued = true;
        Engine.AddAppThreadCoroutine(() => PollUntilModelReady(component, state));
    }

    private static bool PollUntilModelReady(StaticRigidBodyComponent component, State state)
    {
        if (!component.IsActive || !component.AutoGenerateConvexCollidersFromSiblingModel)
        {
            state.Queued = false;
            return true;
        }

        List<ModelComponent> models = ResolveModels(component);
        if (models.Count == 0 || !HasMeshData(models))
            return false;

        state.Queued = false;
        state.Started = true;
        _ = GenerateAndAttachAsync(component, models, state);
        return true;
    }

    private static List<ModelComponent> ResolveModels(StaticRigidBodyComponent component)
    {
        List<ModelComponent> models = [];
        if (component.TargetModelComponents is { Count: > 0 } targets)
        {
            for (int i = 0; i < targets.Count; i++)
                if (targets[i] is ModelComponent model)
                    models.Add(model);
            return models;
        }

        if (component.TargetModelComponent is ModelComponent target)
            models.Add(target);
        else if (component.GetSiblingComponent<ModelComponent>() is { } sibling)
            models.Add(sibling);
        return models;
    }

    private static bool HasMeshData(List<ModelComponent> models)
    {
        for (int i = 0; i < models.Count; i++)
            if (models[i].Meshes.Count > 0 || (models[i].Model?.Meshes.Count ?? 0) > 0)
                return true;
        return false;
    }

    private static async Task GenerateAndAttachAsync(
        StaticRigidBodyComponent component,
        List<ModelComponent> models,
        State state)
    {
        try
        {
            ConvexHullInputCollection inputs = models.Count == 1
                ? ConvexHullUtility.CollectCollisionInputCollection(models[0])
                : ConvexHullUtility.CollectCollisionInputCollection(models, component.Transform);
            List<CoACD.ConvexHullMesh> hulls = [];
            foreach (ConvexHullInputBatch batch in inputs.EnumeratePreferredBatches())
            {
                for (int i = 0; i < batch.Inputs.Count; i++)
                {
                    ConvexHullInput input = batch.Inputs[i];
                    IReadOnlyList<CoACD.ConvexHullMesh>? generated = await CoACD.CalculateAsync(
                        input.Positions,
                        input.Indices).ConfigureAwait(false);
                    if (generated is { Count: > 0 })
                        hulls.AddRange(generated);
                }
                if (hulls.Count > 0)
                    break;
            }

            if (hulls.Count == 0)
                return;

            IReadOnlyList<PhysxConvexMesh> meshes = PhysxConvexHullCooker.CookHulls(
                hulls,
                out _,
                out _,
                requestGpuData: true);
            RuntimeThreadServices.Current.EnqueuePhysicsThread(
                () => AttachMeshes(component, meshes));
        }
        catch (Exception ex)
        {
            Debug.PhysicsException(ex, "Failed to auto-generate static convex colliders.");
        }
        finally
        {
            state.Started = false;
        }
    }

    private static void AttachMeshes(
        StaticRigidBodyComponent component,
        IReadOnlyList<PhysxConvexMesh> meshes)
    {
        if (!component.IsActive || component.RigidBody is not PhysxStaticRigidBody body || body.ShapeCount > 0)
            return;

        PhysxMaterial material = ResolveMaterial(component);
        for (int i = 0; i < meshes.Count; i++)
        {
            unsafe
            {
                PhysxConvexMeshGeometryExtension geometry = new(
                    meshes[i].ConvexMeshPtr,
                    Vector3.One,
                    Quaternion.Identity,
                    tightBounds: false);
                PhysxShape shape = new(
                    geometry,
                    material,
                    PxShapeFlags.SimulationShape | PxShapeFlags.SceneQueryShape | PxShapeFlags.Visualization,
                    isExclusive: true)
                {
                    LocalPose = (component.ShapeOffsetTranslation, component.ShapeOffsetRotation),
                };
                body.AttachShape(shape);
            }
        }
    }

    private static PhysxMaterial ResolveMaterial(StaticRigidBodyComponent component)
    {
        if (component.Material is PhysxMaterial material)
            return material;
        PhysicsMaterialDefinition? definition = component.MaterialDefinition;
        PhysxMaterial created = definition is null
            ? new PhysxMaterial(0.5f, 0.5f, 0.1f)
            : new PhysxMaterial(definition.StaticFriction, definition.DynamicFriction, definition.Restitution)
            {
                Damping = definition.Damping,
            };
        component.Material = created;
        return created;
    }
}