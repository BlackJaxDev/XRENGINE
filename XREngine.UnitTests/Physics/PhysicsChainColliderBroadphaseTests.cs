using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainColliderBroadphaseTests
{
    [Test]
    public void QueryReturnsOnlyIntersectingBoundedShapesPlusPlanes()
    {
        PhysicsChainColliderShape[] shapes =
        [
            PhysicsChainColliderShape.Sphere(Vector3.Zero, 1.0f),
            PhysicsChainColliderShape.Sphere(Vector3.Zero, 1.0f),
            PhysicsChainColliderShape.Plane(Vector3.Zero, Vector3.UnitY),
        ];
        var broadphase = new PhysicsChainColliderBroadphase(shapes);
        Matrix4x4[] poses =
        [
            Matrix4x4.CreateTranslation(2.0f, 0.0f, 0.0f),
            Matrix4x4.CreateTranslation(100.0f, 0.0f, 0.0f),
            Matrix4x4.Identity,
        ];
        broadphase.UpdatePoses(poses).ShouldBeTrue();
        Span<int> candidates = stackalloc int[3];
        Span<int> stack = stackalloc int[broadphase.NodeCount];

        PhysicsChainColliderCandidateQueryResult result = broadphase.Query(
            new PhysicsChainAabb(new Vector3(-1.0f), new Vector3(4.0f)),
            candidates,
            stack);

        result.Succeeded.ShouldBeTrue();
        candidates[..result.CandidateCount].ToArray().ShouldBe([2, 0], ignoreOrder: true);
    }

    [Test]
    public void CandidateOverflowReportsExactRequiredCapacityWithoutWritingPastOutput()
    {
        PhysicsChainColliderShape[] shapes = new PhysicsChainColliderShape[10];
        Array.Fill(shapes, PhysicsChainColliderShape.Sphere(Vector3.Zero, 1.0f));
        var broadphase = new PhysicsChainColliderBroadphase(shapes);
        Span<int> candidates = stackalloc int[3];
        Span<int> stack = stackalloc int[broadphase.NodeCount];

        PhysicsChainColliderCandidateQueryResult result = broadphase.Query(
            new PhysicsChainAabb(new Vector3(-2.0f), new Vector3(2.0f)),
            candidates,
            stack);

        result.CandidateOverflow.ShouldBeTrue();
        result.CandidateCount.ShouldBe(3);
        result.RequiredCandidateCount.ShouldBe(10);
    }

    [Test]
    public void PoseRefitAndQueriesAllocateNothing()
    {
        PhysicsChainColliderShape[] shapes = new PhysicsChainColliderShape[16];
        Array.Fill(shapes, PhysicsChainColliderShape.Box(Vector3.Zero, Vector3.One));
        var broadphase = new PhysicsChainColliderBroadphase(shapes);
        Matrix4x4[] poses = new Matrix4x4[shapes.Length];
        Array.Fill(poses, Matrix4x4.Identity);
        Span<int> candidates = stackalloc int[shapes.Length];
        Span<int> stack = stackalloc int[broadphase.NodeCount];
        var query = new PhysicsChainAabb(new Vector3(-2.0f), new Vector3(2.0f));
        broadphase.Query(query, candidates, stack);

        bool allPosesAccepted = true;
        int lastRequiredCount = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; ++i)
        {
            allPosesAccepted &= broadphase.UpdatePoses(poses);
            lastRequiredCount = broadphase.Query(query, candidates, stack).RequiredCandidateCount;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allPosesAccepted.ShouldBeTrue();
        lastRequiredCount.ShouldBe(shapes.Length);
        allocated.ShouldBe(0L);
    }
}
