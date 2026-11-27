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

        if (component.ColliderShapes.Count > 0 || component.Geometry is not null)
        {
            state.Queued = false;
            return true;
        }

        List<ModelComponent> models = ResolveModels(component);
        if (models.Count == 0 || !HasMeshData(models))
            return false;

        IConvexHullInputProvider? provider = RuntimePhysicsServices.ConvexHullInputs;
        if (provider is null || !provider.TryCollect(component, out ConvexHullInputCollection inputs, out string targetLabel))
        {
            state.Queued = false;
            return true;
        }

        state.Queued = false;
        state.Started = true;
        _ = Task.Run(() => GenerateAndAttachAsync(component, inputs, targetLabel, state));
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
        ConvexHullInputCollection inputs,
        string targetLabel,
        State state)
    {
        try
        {
            CoACD.CoACDParameters parameters = CoACD.CoACDParameters.Default;
            IPhysicsColliderAuthoringService authoring = PhysicsColliderAuthoringServices.Require();
            string? cacheRoot = ConvexHullDiskCache.ResolveCacheRoot();
            int cachedInputCount = 0;
            int generatedInputCount = 0;

            List<CoACD.ConvexHullMesh> hulls = [];
            foreach (ConvexHullInputBatch batch in inputs.EnumeratePreferredBatches())
            {
                for (int i = 0; i < batch.Inputs.Count; i++)
                {
                    ConvexHullInput input = batch.Inputs[i];
                    if (authoring.TryLoadCachedHulls(parameters, in input, cacheRoot, out List<CoACD.ConvexHullMesh> cachedHulls))
                    {
                        cachedInputCount++;
                        if (cachedHulls.Count > 0)
                            hulls.AddRange(cachedHulls);
                        continue;
                    }

                    IReadOnlyList<CoACD.ConvexHullMesh>? generated = await authoring.GenerateAsync(
                        input.Positions,
                        input.Indices,
                        parameters,
                        CancellationToken.None).ConfigureAwait(false);
                    generatedInputCount++;
                    if (generated is { Count: > 0 })
                        hulls.AddRange(generated);
                    authoring.StoreCachedHulls(parameters, in input, cacheRoot, generated ?? Array.Empty<CoACD.ConvexHullMesh>());
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

            Debug.Physics(
                $"[StaticColliderAuthoring] Prepared {hulls.Count} convex hull(s) for '{targetLabel}' ({cachedInputCount} cached, {generatedInputCount} generated).");
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
