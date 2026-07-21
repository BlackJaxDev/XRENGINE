using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainCpuSharedColliderSetTests
{
    [Test]
    public void SmallSetsBypassBroadphaseAndPreserveWorldPose()
    {
        PhysicsChainColliderShape[] shapes =
        [
            PhysicsChainColliderShape.Sphere(Vector3.Zero, 1.0f),
            PhysicsChainColliderShape.Plane(Vector3.Zero, Vector3.UnitY),
        ];
        var shared = new PhysicsChainCpuSharedColliderSet(new PhysicsChainColliderSet(1L, shapes, 2UL));
        shared.TrySetPose(0, Matrix4x4.CreateTranslation(4.0f, 0.0f, 0.0f)).ShouldBeTrue();
        Span<PhysicsChainCpuCollider> colliders = stackalloc PhysicsChainCpuCollider[2];

        shared.TryBuildCandidates(default, colliders, default, default, out int count, out bool fallback).ShouldBeTrue();

        count.ShouldBe(2);
        fallback.ShouldBeFalse();
        colliders[0].Center.ShouldBe(new Vector3(4.0f, 0.0f, 0.0f));
        shared.GetSnapshot().SmallSetBypassCount.ShouldBe(1L);
    }

    [Test]
    public void LargeSetCostUsesGeneratedCandidates()
    {
        PhysicsChainColliderShape[] shapes = new PhysicsChainColliderShape[8];
        Array.Fill(shapes, PhysicsChainColliderShape.Sphere(Vector3.Zero, 0.5f));
        var shared = new PhysicsChainCpuSharedColliderSet(new PhysicsChainColliderSet(3L, shapes, 4UL));
        for (int index = 0; index < shapes.Length; ++index)
            shared.TrySetPose(index, Matrix4x4.CreateTranslation(index * 10.0f, 0.0f, 0.0f)).ShouldBeTrue();
        Span<PhysicsChainCpuCollider> colliders = stackalloc PhysicsChainCpuCollider[shapes.Length];
        Span<int> indices = stackalloc int[shapes.Length];
        Span<int> traversal = stackalloc int[shared.RequiredTraversalStackLength];

        shared.TryBuildCandidates(
            new PhysicsChainAabb(new Vector3(-1.0f), new Vector3(1.0f)),
            colliders,
            indices,
            traversal,
            out int count,
            out bool fallback).ShouldBeTrue();

        count.ShouldBe(1);
        fallback.ShouldBeFalse();
        shared.GetSnapshot().CandidateCount.ShouldBe(1L);
    }

    [Test]
    public void CandidateOverflowFallsBackToFullSetWithoutAllocation()
    {
        PhysicsChainColliderShape[] shapes = new PhysicsChainColliderShape[8];
        Array.Fill(shapes, PhysicsChainColliderShape.Sphere(Vector3.Zero, 1.0f));
        var shared = new PhysicsChainCpuSharedColliderSet(new PhysicsChainColliderSet(5L, shapes, 6UL));
        Span<PhysicsChainCpuCollider> colliders = stackalloc PhysicsChainCpuCollider[shapes.Length];
        Span<int> indices = stackalloc int[2];
        Span<int> traversal = stackalloc int[shared.RequiredTraversalStackLength];
        var bounds = new PhysicsChainAabb(new Vector3(-2.0f), new Vector3(2.0f));
        shared.TryBuildCandidates(bounds, colliders, indices, traversal, out _, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = shared.TryBuildCandidates(
            bounds,
            colliders,
            indices,
            traversal,
            out int count,
            out bool fallback);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        succeeded.ShouldBeTrue();
        count.ShouldBe(shapes.Length);
        fallback.ShouldBeTrue();
        allocated.ShouldBe(0L);
        shared.GetSnapshot().FullSetFallbackCount.ShouldBe(2L);
    }
}
