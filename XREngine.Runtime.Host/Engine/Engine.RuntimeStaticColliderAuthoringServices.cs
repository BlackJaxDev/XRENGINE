using System.Runtime.CompilerServices;
using XREngine.Components.Physics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Tools;
using XREngine.Scene;
using XREngine.Scene.Physics;

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
            IConvexHullInputProvider? provider = RuntimePhysicsServices.ConvexHullInputs;
            if (provider is null || !provider.TryCollect(component, out ConvexHullInputCollection inputs, out _))
                return;
            List<CoACD.ConvexHullMesh> hulls = [];
            foreach (ConvexHullInputBatch batch in inputs.EnumeratePreferredBatches())
            {
                for (int i = 0; i < batch.Inputs.Count; i++)
                {
                    ConvexHullInput input = batch.Inputs[i];
                    IReadOnlyList<CoACD.ConvexHullMesh>? generated = await PhysicsColliderAuthoringServices.Require().GenerateAsync(
                        input.Positions,
                        input.Indices,
                        CoACD.CoACDParameters.Default,
                        CancellationToken.None).ConfigureAwait(false);
                    if (generated is { Count: > 0 })
                        hulls.AddRange(generated);
                }
                if (hulls.Count > 0)
                    break;
            }

            if (hulls.Count == 0)
                return;

            AbstractPhysicsScene? scene = component.WorldAs<IRuntimePhysicsWorldContext>()?.PhysicsScene;
            if (scene?.BackendService is not IPhysicsConvexHullInstaller installer)
                throw new NotSupportedException(
                    $"Physics backend '{scene?.GetType().Name ?? "<none>"}' does not install convex-hull authoring support.");
            installer.PrepareAndAttach(component, hulls);
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

}
