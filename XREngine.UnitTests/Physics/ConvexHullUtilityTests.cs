using System.Numerics;
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
        firstPass[0].ShouldBe(expectedHull);
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
}
