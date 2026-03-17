using System.Numerics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Physics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Tools;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class ConvexHullUtilityTests
{
    private string? _cacheRoot;

    [SetUp]
    public void SetUp()
    {
        _cacheRoot = Path.Combine(Path.GetTempPath(), $"xre_coacd_cache_test_{Guid.NewGuid():N}");
        ConvexHullDiskCache.CacheRootOverride = _cacheRoot;
    }

    [TearDown]
    public void TearDown()
    {
        ConvexHullDiskCache.CacheRootOverride = null;

        if (!string.IsNullOrWhiteSpace(_cacheRoot) && Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, recursive: true);

        _cacheRoot = null;
    }

    [Test]
    public void CollectCollisionInputs_UsesRuntimeMeshesWhenAvailable()
    {
        var (_, modelComponent) = CreateComponentPair();
        AttachModel(modelComponent);

        var inputs = ConvexHullUtility.CollectCollisionInputs(modelComponent);

        inputs.ShouldNotBeEmpty();
        inputs[0].Positions.Length.ShouldBe(6);
        inputs[0].Indices.Length.ShouldBe(6);
    }

    [Test]
    public void CollectCollisionInputs_FallsBackToAssetMeshesWhenRuntimeUnavailable()
    {
        var (_, modelComponent) = CreateComponentPair();
        AttachModel(modelComponent);
        modelComponent.Meshes.Clear();

        var inputs = ConvexHullUtility.CollectCollisionInputs(modelComponent);

        inputs.ShouldHaveSingleItem();
        inputs[0].Positions.Length.ShouldBe(6);
        inputs[0].Indices.Length.ShouldBe(6);
    }

    [Test]
    public async Task CreateConvexDecompositionAsync_UsesRunnerAndCachesResults()
    {
        var (physicsComponent, modelComponent) = CreateComponentPair();
        AttachModel(modelComponent);

        var expectedHull = new CoACD.ConvexHullMesh(
            new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ },
            new[] { 0, 1, 2, 0, 2, 3 });
        physicsComponent.SetRunnerResult([expectedHull]);

        var firstPass = await physicsComponent.CreateConvexDecompositionAsync().ConfigureAwait(false);
        firstPass.ShouldHaveSingleItem();
        firstPass[0].Vertices.ShouldBe(expectedHull.Vertices);
        firstPass[0].Indices.ShouldBe(expectedHull.Indices);
        physicsComponent.RunnerCallCount.ShouldBe(1);

        var secondPass = await physicsComponent.CreateConvexDecompositionAsync().ConfigureAwait(false);
        secondPass.ShouldHaveSingleItem();
        physicsComponent.RunnerCallCount.ShouldBe(1, "cached hulls should avoid recomputing");
    }

    [Test]
    public async Task CreateConvexDecompositionAsync_UsesAssetMeshesWhenRuntimeMeshesMissing()
    {
        var (physicsComponent, modelComponent) = CreateComponentPair();
        AttachModel(modelComponent);
        modelComponent.Meshes.Clear();

        physicsComponent.SetRunnerResult([
            new CoACD.ConvexHullMesh(
                new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
                new[] { 0, 1, 2 })
        ]);

        var hulls = await physicsComponent.CreateConvexDecompositionAsync().ConfigureAwait(false);

        hulls.ShouldHaveSingleItem();
        physicsComponent.RunnerCallCount.ShouldBe(1);
        physicsComponent.LastRunnerPositions.ShouldNotBeNull();
        physicsComponent.LastRunnerPositions!.Length.ShouldBe(6);
    }

    [Test]
    public async Task CreateConvexDecompositionAsync_ReusesDiskCachedHullsAcrossComponents()
    {
        var firstExpectedHull = new CoACD.ConvexHullMesh(
            new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ },
            new[] { 0, 1, 2, 0, 2, 3 });

        var (firstPhysicsComponent, firstModelComponent) = CreateComponentPair();
        AttachModel(firstModelComponent);
        firstPhysicsComponent.SetRunnerResult([firstExpectedHull]);

        var firstPass = await firstPhysicsComponent.CreateConvexDecompositionAsync().ConfigureAwait(false);
        firstPass.ShouldHaveSingleItem();
        firstPhysicsComponent.RunnerCallCount.ShouldBe(1);

        var (secondPhysicsComponent, secondModelComponent) = CreateComponentPair();
        AttachModel(secondModelComponent);

        var secondPass = await secondPhysicsComponent.CreateConvexDecompositionAsync().ConfigureAwait(false);
        secondPass.ShouldHaveSingleItem();
        secondPass[0].Vertices.ShouldBe(firstExpectedHull.Vertices);
        secondPass[0].Indices.ShouldBe(firstExpectedHull.Indices);
        secondPhysicsComponent.RunnerCallCount.ShouldBe(0, "disk cache should satisfy the second component without rerunning CoACD");
    }

    [Test]
    public async Task CreateConvexDecompositionAsync_PublishesPartialHullSnapshotsBeforeCompletion()
    {
        var node = new SceneNode("ConvexHullPartialPreviewTests");
        var (physicsComponent, modelComponent) = node.AddComponents<SequencedPhysicsActorComponent, ModelComponent>();
        physicsComponent.ShouldNotBeNull();
        modelComponent.ShouldNotBeNull();

        AttachModelWithTwoSubmeshes(modelComponent!);

        var firstHull = new CoACD.ConvexHullMesh(
            new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ },
            new[] { 0, 1, 2, 0, 2, 3 });
        var secondHull = new CoACD.ConvexHullMesh(
            new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitZ },
            new[] { 0, 1, 2 });

        physicsComponent!.SetStepResults([firstHull], [secondHull]);

        Task<List<CoACD.ConvexHullMesh>> generationTask = physicsComponent.CreateConvexDecompositionAsync();

        await physicsComponent.WaitForSecondCallAsync().ConfigureAwait(false);

        var partialHulls = physicsComponent.GetCachedConvexHulls();
        partialHulls.ShouldNotBeNull();
        partialHulls.Count.ShouldBe(1);
        partialHulls[0].Vertices.ShouldBe(firstHull.Vertices);
        partialHulls[0].Indices.ShouldBe(firstHull.Indices);

        physicsComponent.ReleaseSecondCall();

        var completedHulls = await generationTask.ConfigureAwait(false);
        completedHulls.Count.ShouldBe(2);
    }

    private static (TestPhysicsActorComponent physics, ModelComponent model) CreateComponentPair()
    {
        var node = new SceneNode("ConvexHullTests");
        var (physics, model) = node.AddComponents<TestPhysicsActorComponent, ModelComponent>();
        physics.ShouldNotBeNull();
        model.ShouldNotBeNull();
        return (physics!, model!);
    }

    private static void AttachModel(ModelComponent component)
    {
        var mesh = XRMesh.CreateTriangles(
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(0f, 1f, 0f),
            new Vector3(0f, 1f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(1f, 1f, 0f));

        var lod = new SubMeshLOD(material: null, mesh, maxVisibleDistance: 0f);
        var subMesh = new SubMesh(lod);
        component.Model = new Model(subMesh);
    }

    private static void AttachModelWithTwoSubmeshes(ModelComponent component)
    {
        var firstMesh = XRMesh.CreateTriangles(
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(0f, 1f, 0f));
        var secondMesh = XRMesh.CreateTriangles(
            new Vector3(0f, 0f, 1f),
            new Vector3(1f, 0f, 1f),
            new Vector3(0f, 1f, 1f));

        var firstSubMesh = new SubMesh(new SubMeshLOD(material: null, firstMesh, maxVisibleDistance: 0f));
        var secondSubMesh = new SubMesh(new SubMeshLOD(material: null, secondMesh, maxVisibleDistance: 0f));
        component.Model = new Model([firstSubMesh, secondSubMesh]);
    }

    private sealed class TestPhysicsActorComponent : PhysicsActorComponent
    {
        private readonly StubRunner _runner = new();

        public int RunnerCallCount => _runner.CallCount;
        public Vector3[]? LastRunnerPositions => _runner.LastPositions;

        protected override IConvexDecompositionRunner ConvexDecompositionRunner => _runner;

        public void SetRunnerResult(IReadOnlyList<CoACD.ConvexHullMesh>? hulls)
            => _runner.Result = hulls;

        private sealed class StubRunner : IConvexDecompositionRunner
        {
            public int CallCount;
            public IReadOnlyList<CoACD.ConvexHullMesh>? Result;
            public Vector3[]? LastPositions;

            public Task<IReadOnlyList<CoACD.ConvexHullMesh>?> GenerateAsync(
                Vector3[] positions,
                int[] indices,
                CoACD.CoACDParameters parameters,
                CancellationToken cancellationToken)
            {
                CallCount++;
                LastPositions = positions;
                return Task.FromResult(Result);
            }
        }
    }

    private sealed class SequencedPhysicsActorComponent : PhysicsActorComponent
    {
        private readonly SequencedRunner _runner = new();

        protected override IConvexDecompositionRunner ConvexDecompositionRunner => _runner;

        public void SetStepResults(IReadOnlyList<CoACD.ConvexHullMesh> firstResult, IReadOnlyList<CoACD.ConvexHullMesh> secondResult)
            => _runner.SetResults(firstResult, secondResult);

        public Task WaitForSecondCallAsync()
            => _runner.WaitForSecondCallAsync();

        public void ReleaseSecondCall()
            => _runner.ReleaseSecondCall();

        private sealed class SequencedRunner : IConvexDecompositionRunner
        {
            private readonly TaskCompletionSource _secondCallReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _releaseSecondCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private IReadOnlyList<CoACD.ConvexHullMesh>? _firstResult;
            private IReadOnlyList<CoACD.ConvexHullMesh>? _secondResult;
            private int _callCount;

            public void SetResults(IReadOnlyList<CoACD.ConvexHullMesh> firstResult, IReadOnlyList<CoACD.ConvexHullMesh> secondResult)
            {
                _firstResult = firstResult;
                _secondResult = secondResult;
            }

            public Task WaitForSecondCallAsync()
                => _secondCallReached.Task;

            public void ReleaseSecondCall()
                => _releaseSecondCall.TrySetResult();

            public async Task<IReadOnlyList<CoACD.ConvexHullMesh>?> GenerateAsync(
                Vector3[] positions,
                int[] indices,
                CoACD.CoACDParameters parameters,
                CancellationToken cancellationToken)
            {
                _callCount++;
                if (_callCount == 1)
                    return _firstResult;

                _secondCallReached.TrySetResult();
                await _releaseSecondCall.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return _secondResult;
            }
        }
    }
}
