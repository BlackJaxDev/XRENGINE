using MagicPhysX;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Tools;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;

namespace XREngine.Components.Physics
{
    public abstract class PhysicsActorComponent : XRComponent
    {
        private readonly Dictionary<CoACD.CoACDParameters, List<CoACD.ConvexHullMesh>> _cachedConvexHulls = new();
        private readonly Dictionary<(CoACD.CoACDParameters parameters, PxConvexFlags flags, bool requestGpuData), List<PhysxConvexMesh>> _physxMeshCache = new();

        public IAbstractPhysicsActor? PhysicsActor { get; }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            if (World is not null && PhysicsActor is not null)
                World.PhysicsScene.AddActor(PhysicsActor);
        }
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();

            if (World is not null && PhysicsActor is not null)
                World.PhysicsScene.RemoveActor(PhysicsActor);
        }

        public async Task<List<CoACD.ConvexHullMesh>> CreateConvexDecompositionAsync(CoACD.CoACDParameters? parameters = null)
        {
            var config = parameters ?? CoACD.CoACDParameters.Default;

            if (_cachedConvexHulls.TryGetValue(config, out var cached) && cached.Count > 0)
                return [.. cached];

            var modelComponent = GetSiblingComponent<ModelComponent>();
            if (modelComponent is null)
                return [];

            var results = new List<CoACD.ConvexHullMesh>();

            foreach (var renderableMesh in modelComponent.Meshes)
            {
                var mesh = renderableMesh.CurrentLODMesh;
                if (mesh is null)
                    continue;

                var vertices = mesh.Vertices.Select(v => v.Position).ToArray();
                var indices = mesh.GetIndices();

                if (vertices.Length == 0 || indices == null || indices.Length == 0)
                    continue;

                var hulls = await CoACD.CalculateAsync(vertices, indices, config).ConfigureAwait(false);

                if (hulls is null || hulls.Count == 0)
                    continue;

                results.AddRange(hulls);
            }

            if (results.Count > 0)
            {
                _cachedConvexHulls[config] = [.. results];
                InvalidatePhysxMeshCache(config);
            }

            return results;
        }

        public async Task<IReadOnlyList<PhysxConvexMesh>> CreatePhysxConvexMeshesAsync(
            CoACD.CoACDParameters? parameters = null,
            PxConvexFlags extraFlags = 0,
            bool requestGpuData = true,
            IReadOnlyList<CoACD.ConvexHullMesh>? cachedHulls = null)
        {
            var config = parameters ?? CoACD.CoACDParameters.Default;
            var cacheKey = (config, extraFlags, requestGpuData);

            if (_physxMeshCache.TryGetValue(cacheKey, out var cachedMeshes) && cachedMeshes.Count > 0)
                return cachedMeshes;

            var hulls = cachedHulls ?? GetCachedHullReference(config);
            if (hulls is null || hulls.Count == 0)
                hulls = await CreateConvexDecompositionAsync(config).ConfigureAwait(false);

            if (hulls.Count == 0)
                return [];

            var cooked = PhysxConvexHullCooker.CookHulls(hulls, extraFlags, requestGpuData);
            var meshList = cooked is List<PhysxConvexMesh> list ? list : [.. cooked];
            _physxMeshCache[cacheKey] = meshList;
            return meshList;
        }

        protected override void OnDestroying()
        {
            ClearPhysxMeshCache();
            base.OnDestroying();
        }

        public void GenerateConvexHullsFromModel()
        {
            GenerateConvexHullsFromModelAsync().GetAwaiter().GetResult();
        }
        public async Task GenerateConvexHullsFromModelAsync()
        {
            try
            {
                var defaultParams = CoACD.CoACDParameters.Default;

                IReadOnlyList<CoACD.ConvexHullMesh>? preparedHulls = null;
                if (!HasCachedHulls(defaultParams) && GetSiblingComponent<ModelComponent>() is not null)
                    preparedHulls = await CreateConvexDecompositionAsync(defaultParams).ConfigureAwait(false);
                else
                    preparedHulls = GetCachedHullReference(defaultParams);

                if (World?.PhysicsScene is PhysxScene && PhysicsActor is PhysxActor)
                    await CreatePhysxConvexMeshesAsync(defaultParams, cachedHulls: preparedHulls).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to prepare convex assets for {GetType().Name}.");
            }
        }

        private bool HasCachedHulls(CoACD.CoACDParameters parameters)
            => _cachedConvexHulls.TryGetValue(parameters, out var cached) && cached.Count > 0;

        private IReadOnlyList<CoACD.ConvexHullMesh>? GetCachedHullReference(CoACD.CoACDParameters parameters)
        {
            return _cachedConvexHulls.TryGetValue(parameters, out var cached) && cached.Count > 0
                ? cached
                : null;
        }

        private void InvalidatePhysxMeshCache(CoACD.CoACDParameters parameters)
        {
            if (_physxMeshCache.Count == 0)
                return;

            var keysToRemove = _physxMeshCache.Keys
                .Where(key => key.parameters.Equals(parameters))
                .ToList();

            foreach (var key in keysToRemove)
            {
                ReleasePhysxMeshes(_physxMeshCache[key]);
                _physxMeshCache.Remove(key);
            }
        }

        private void ClearPhysxMeshCache()
        {
            if (_physxMeshCache.Count == 0)
                return;

            foreach (var meshes in _physxMeshCache.Values)
                ReleasePhysxMeshes(meshes);
            _physxMeshCache.Clear();
        }

        private static void ReleasePhysxMeshes(List<PhysxConvexMesh> meshes)
        {
            foreach (var mesh in meshes)
            {
                if (mesh is null)
                    continue;

                try
                {
                    mesh.Release();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, "Failed to release PhysX convex mesh.");
                }
            }
        }
    }
}
