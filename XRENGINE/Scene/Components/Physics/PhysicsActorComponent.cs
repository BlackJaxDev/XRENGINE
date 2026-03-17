using MagicPhysX;
using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly object _convexHullGenerationStatusLock = new();

        private ConvexHullGenerationStatus _convexHullGenerationStatus = new(
            InProgress: false,
            ActiveMessage: null,
            LastMessage: null,
            Progress: null,
            StartedAtUtc: null);

        public IAbstractPhysicsActor? PhysicsActor { get; }

        /// <summary>
        /// Resolves the ModelComponent to use for collision mesh extraction.
        /// Subclasses can override to provide a specific target (e.g. when multiple
        /// ModelComponents exist on the same node).
        /// </summary>
        protected virtual ModelComponent? ResolveModelComponentForColliders()
            => GetSiblingComponent<ModelComponent>();

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            if (World is not null && PhysicsActor is not null)
                WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene.AddActor(PhysicsActor);
        }
        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();

            if (World is not null && PhysicsActor is not null)
                WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene.RemoveActor(PhysicsActor);
        }

        protected (Vector3 position, Quaternion rotation) GetSpawnPose()
        {
            var matrix = Transform.WorldMatrix;
            Matrix4x4.Decompose(matrix, out _, out Quaternion rotation, out Vector3 translation);
            return (translation, rotation);
        }

        private static readonly IConvexDecompositionRunner s_defaultRunner = new CoAcdRunner();

        protected virtual IConvexDecompositionRunner ConvexDecompositionRunner => s_defaultRunner;

        public ConvexHullGenerationStatus GetConvexHullGenerationStatus()
        {
            lock (_convexHullGenerationStatusLock)
                return _convexHullGenerationStatus;
        }

        public async Task<List<CoACD.ConvexHullMesh>> CreateConvexDecompositionAsync(
            CoACD.CoACDParameters? parameters = null,
            IProgress<ConvexHullGenerationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var config = parameters ?? CoACD.CoACDParameters.Default;

            if (_cachedConvexHulls.TryGetValue(config, out var cached) && cached.Count > 0)
            {
                progress?.Report(ConvexHullGenerationProgress.FromCache(cached.Count));
                return [.. cached];
            }

            var modelComponent = ResolveModelComponentForColliders();
            if (modelComponent is null)
                return [];

            var results = new List<CoACD.ConvexHullMesh>();

            var inputs = ConvexHullUtility.CollectCollisionInputs(modelComponent);
            int totalInputs = inputs.Count;
            if (totalInputs == 0)
            {
                progress?.Report(new ConvexHullGenerationProgress(0, 0, "No collision meshes available."));
                return results;
            }

            progress?.Report(new ConvexHullGenerationProgress(0, totalInputs, "Starting convex decomposition."));

            for (int i = 0; i < totalInputs; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var input = inputs[i];
                progress?.Report(new ConvexHullGenerationProgress(i, totalInputs, $"Decomposing mesh {i + 1} of {totalInputs}..."));

                if (ConvexHullDiskCache.TryLoad(config, input, out var cachedInputHulls))
                {
                    if (cachedInputHulls.Count > 0)
                    {
                        results.AddRange(cachedInputHulls);
                        UpdateCachedHullSnapshot(config, results);
                    }

                    progress?.Report(new ConvexHullGenerationProgress(
                        i + 1,
                        totalInputs,
                        $"Loaded cached convex hulls for mesh {i + 1} of {totalInputs}."));
                    continue;
                }

                var hulls = await ConvexDecompositionRunner
                    .GenerateAsync(input.Positions, input.Indices, config, cancellationToken)
                    .ConfigureAwait(false);

                IReadOnlyList<CoACD.ConvexHullMesh> generatedHulls = hulls ?? Array.Empty<CoACD.ConvexHullMesh>();
                if (generatedHulls.Count > 0)
                {
                    results.AddRange(generatedHulls);
                    UpdateCachedHullSnapshot(config, results);
                }

                ConvexHullDiskCache.TryStore(config, input, generatedHulls);

                progress?.Report(new ConvexHullGenerationProgress(i + 1, totalInputs, $"Completed mesh {i + 1} of {totalInputs}."));
            }

            if (results.Count > 0)
            {
                UpdateCachedHullSnapshot(config, results);
                progress?.Report(new ConvexHullGenerationProgress(totalInputs, totalInputs, "Convex hulls cached."));
            }
            else
            {
                progress?.Report(new ConvexHullGenerationProgress(totalInputs, totalInputs, "No convex hulls generated."));
            }

            return results;
        }

        public async Task<IReadOnlyList<PhysxConvexMesh>> CreatePhysxConvexMeshesAsync(
            CoACD.CoACDParameters? parameters = null,
            PxConvexFlags extraFlags = 0,
            bool requestGpuData = true,
            IReadOnlyList<CoACD.ConvexHullMesh>? cachedHulls = null,
            IProgress<ConvexHullGenerationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var config = parameters ?? CoACD.CoACDParameters.Default;
            var cacheKey = (config, extraFlags, requestGpuData);

            if (_physxMeshCache.TryGetValue(cacheKey, out var cachedMeshes) && cachedMeshes.Count > 0)
                return cachedMeshes;

            var hulls = cachedHulls ?? GetCachedHullReference(config);
            if (hulls is null || hulls.Count == 0)
                hulls = await CreateConvexDecompositionAsync(config, progress, cancellationToken).ConfigureAwait(false);

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
        public async Task GenerateConvexHullsFromModelAsync(
            IProgress<ConvexHullGenerationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            BeginConvexHullGeneration("Preparing convex hull data...");

            try
            {
                var defaultParams = CoACD.CoACDParameters.Default;
                var trackedProgress = CreateTrackedConvexHullProgress(progress);

                IReadOnlyList<CoACD.ConvexHullMesh>? preparedHulls = null;
                if (!HasCachedHulls(defaultParams) && ResolveModelComponentForColliders() is not null)
                    preparedHulls = await CreateConvexDecompositionAsync(defaultParams, trackedProgress, cancellationToken).ConfigureAwait(false);
                else
                {
                    preparedHulls = GetCachedHullReference(defaultParams);
                    if (preparedHulls is not null)
                        trackedProgress.Report(ConvexHullGenerationProgress.FromCache(preparedHulls.Count));
                }

                if (WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene is PhysxScene && PhysicsActor is PhysxActor)
                {
                    ReportConvexHullGenerationMessage("Cooking PhysX convex meshes...");
                    await CreatePhysxConvexMeshesAsync(defaultParams, cachedHulls: preparedHulls, progress: trackedProgress, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                CompleteConvexHullGeneration(preparedHulls is { Count: > 0 }
                    ? "Convex hulls cached successfully."
                    : "No convex hulls generated.");
            }
            catch (Exception ex)
            {
                CompleteConvexHullGeneration($"Failed: {ex.Message}");
                Debug.PhysicsException(ex, $"Failed to prepare convex assets for {GetType().Name}.");
            }
        }

        public readonly record struct ConvexHullGenerationProgress(
            int CompletedInputs,
            int TotalInputs,
            string Message,
            bool UsedCache = false)
        {
            public float Percentage
            {
                get
                {
                    if (UsedCache)
                        return 1f;

                    if (TotalInputs <= 0)
                        return 0f;

                    float ratio = (float)CompletedInputs / TotalInputs;
                    if (ratio < 0f)
                        return 0f;
                    if (ratio > 1f)
                        return 1f;
                    return ratio;
                }
            }

            public static ConvexHullGenerationProgress FromCache(int hullCount)
                => new(hullCount, hullCount, "Using cached convex hulls.", true);
        }

        public readonly record struct ConvexHullGenerationStatus(
            bool InProgress,
            string? ActiveMessage,
            string? LastMessage,
            ConvexHullGenerationProgress? Progress,
            DateTimeOffset? StartedAtUtc);

        private IProgress<ConvexHullGenerationProgress> CreateTrackedConvexHullProgress(IProgress<ConvexHullGenerationProgress>? progress)
            => new Progress<ConvexHullGenerationProgress>(state =>
            {
                ReportConvexHullGenerationProgress(state);
                progress?.Report(state);
            });

        protected void BeginConvexHullGeneration(string initialMessage)
        {
            lock (_convexHullGenerationStatusLock)
            {
                _convexHullGenerationStatus = new ConvexHullGenerationStatus(
                    InProgress: true,
                    ActiveMessage: initialMessage,
                    LastMessage: null,
                    Progress: null,
                    StartedAtUtc: DateTimeOffset.UtcNow);
            }
        }

        protected void ReportConvexHullGenerationMessage(string message)
        {
            lock (_convexHullGenerationStatusLock)
            {
                _convexHullGenerationStatus = _convexHullGenerationStatus with
                {
                    InProgress = true,
                    ActiveMessage = message,
                    StartedAtUtc = _convexHullGenerationStatus.StartedAtUtc ?? DateTimeOffset.UtcNow,
                };
            }
        }

        protected void ReportConvexHullGenerationProgress(ConvexHullGenerationProgress progress)
        {
            lock (_convexHullGenerationStatusLock)
            {
                _convexHullGenerationStatus = _convexHullGenerationStatus with
                {
                    InProgress = true,
                    ActiveMessage = progress.Message,
                    Progress = progress,
                    StartedAtUtc = _convexHullGenerationStatus.StartedAtUtc ?? DateTimeOffset.UtcNow,
                };
            }
        }

        protected void CompleteConvexHullGeneration(string? lastMessage)
        {
            lock (_convexHullGenerationStatusLock)
            {
                _convexHullGenerationStatus = _convexHullGenerationStatus with
                {
                    InProgress = false,
                    ActiveMessage = null,
                    LastMessage = lastMessage,
                    Progress = null,
                    StartedAtUtc = null,
                };
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

        public IReadOnlyList<CoACD.ConvexHullMesh>? GetCachedConvexHulls(CoACD.CoACDParameters? parameters = null)
        {
            var config = parameters ?? CoACD.CoACDParameters.Default;
            return GetCachedHullReference(config);
        }

        private void UpdateCachedHullSnapshot(CoACD.CoACDParameters parameters, List<CoACD.ConvexHullMesh> hulls)
        {
            if (hulls.Count == 0)
                return;

            _cachedConvexHulls[parameters] = [.. hulls];
            InvalidatePhysxMeshCache(parameters);
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
                    Debug.PhysicsException(ex, "Failed to release PhysX convex mesh.");
                }
            }
        }

        protected interface IConvexDecompositionRunner
        {
            Task<IReadOnlyList<CoACD.ConvexHullMesh>?> GenerateAsync(
                Vector3[] positions,
                int[] indices,
                CoACD.CoACDParameters parameters,
                CancellationToken cancellationToken);
        }

        private sealed class CoAcdRunner : IConvexDecompositionRunner
        {
            public Task<IReadOnlyList<CoACD.ConvexHullMesh>?> GenerateAsync(
                Vector3[] positions,
                int[] indices,
                CoACD.CoACDParameters parameters,
                CancellationToken cancellationToken)
                => CoACD.CalculateAsync(positions, indices, parameters, cancellationToken);
        }
    }
}
