using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainColliderRuntimeSetTests
{
    [Test]
    public void DirtyPoseRangeRefitsSharedBvhAndClearsOnlyAfterSuccess()
    {
        PhysicsChainColliderShape[] shapes =
        [
            PhysicsChainColliderShape.Sphere(Vector3.Zero, 0.5f),
            PhysicsChainColliderShape.Box(Vector3.Zero, Vector3.One),
        ];
        var set = new PhysicsChainColliderSet(17L, shapes, 23UL);
        var runtime = new PhysicsChainColliderRuntimeSet(set);

        long initialRefits = runtime.GetSnapshot().RefitCount;
        runtime.TrySetPose(1, Matrix4x4.CreateTranslation(10.0f, 0.0f, 0.0f)).ShouldBeTrue();
        runtime.Poses.DirtyStart.ShouldBe(1);
        runtime.Poses.DirtyCount.ShouldBe(1);

        Span<int> candidates = stackalloc int[2];
        Span<int> traversal = stackalloc int[runtime.RequiredTraversalStackLength];
        PhysicsChainColliderCandidateQueryResult result = runtime.QueryCandidates(
            new PhysicsChainAabb(new Vector3(8.5f, -1.5f, -1.5f), new Vector3(11.5f, 1.5f, 1.5f)),
            candidates,
            traversal);

        result.Succeeded.ShouldBeTrue();
        result.CandidateCount.ShouldBe(1);
        candidates[0].ShouldBe(1);
        runtime.Poses.DirtyCount.ShouldBe(0);
        runtime.GetSnapshot().RefitCount.ShouldBe(initialRefits + 1L);
    }

    [Test]
    public void CandidateOverflowIsVisibleAndRequiresConservativeFullSetFallback()
    {
        PhysicsChainColliderShape[] shapes = new PhysicsChainColliderShape[8];
        Array.Fill(shapes, PhysicsChainColliderShape.Sphere(Vector3.Zero, 1.0f));
        var runtime = new PhysicsChainColliderRuntimeSet(new PhysicsChainColliderSet(4L, shapes, 5UL));
        Span<int> candidates = stackalloc int[2];
        Span<int> traversal = stackalloc int[runtime.RequiredTraversalStackLength];

        PhysicsChainColliderCandidateQueryResult result = runtime.QueryCandidates(
            new PhysicsChainAabb(new Vector3(-2.0f), new Vector3(2.0f)),
            candidates,
            traversal);

        result.CandidateOverflow.ShouldBeTrue();
        result.RequiredCandidateCount.ShouldBe(shapes.Length);
        runtime.GetSnapshot().CandidateOverflowCount.ShouldBe(1L);
    }

    [Test]
    public void UnchangedQueriesAllocateNoManagedMemory()
    {
        PhysicsChainColliderShape[] shapes = new PhysicsChainColliderShape[16];
        Array.Fill(shapes, PhysicsChainColliderShape.Sphere(Vector3.Zero, 1.0f));
        var runtime = new PhysicsChainColliderRuntimeSet(new PhysicsChainColliderSet(6L, shapes, 7UL));
        Span<int> candidates = stackalloc int[shapes.Length];
        Span<int> traversal = stackalloc int[runtime.RequiredTraversalStackLength];
        var bounds = new PhysicsChainAabb(new Vector3(-2.0f), new Vector3(2.0f));
        runtime.QueryCandidates(bounds, candidates, traversal);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; ++iteration)
            runtime.QueryCandidates(bounds, candidates, traversal);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0L);
    }
}
