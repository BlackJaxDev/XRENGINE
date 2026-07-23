using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainCollisionScalingTests
{
    [Test]
    public void AuthoredShapesPrecomputeInvariantTerms()
    {
        PhysicsChainColliderShape capsule = PhysicsChainColliderShape.Capsule(
            new Vector3(1.0f, 2.0f, 3.0f),
            new Vector3(0.0f, 3.0f, 4.0f),
            0.75f);

        capsule.AxisLengthSquared.ShouldBe(25.0f);
        capsule.AxisLength.ShouldBe(5.0f);
        capsule.InverseAxisLengthSquared.ShouldBe(0.04f, 1e-6f);
        Vector3.Distance(capsule.UnitAxis, new Vector3(0.0f, 0.6f, 0.8f)).ShouldBeLessThan(1e-6f);
        capsule.RadiusSquared.ShouldBe(0.5625f);
        capsule.Diameter.ShouldBe(1.5f);
        capsule.LocalBoundsExtents.ShouldBe(new Vector3(0.75f, 3.75f, 4.75f));

        PhysicsChainColliderShape plane = PhysicsChainColliderShape.Plane(
            new Vector3(0.0f, 2.0f, 0.0f),
            Vector3.UnitY * 4.0f);
        plane.UnitAxis.ShouldBe(Vector3.UnitY);
        plane.LocalPlaneDistance.ShouldBe(-2.0f);
    }

    [Test]
    public void CacheReportsDeduplicationAndUniqueShapeMemory()
    {
        var context = new TestWorldContext();
        var node = new SceneNode();
        PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;
        node.World = context;
        context.Run(ETickGroup.PostPhysics);
        PhysicsChainWorld.TryGet(context, out PhysicsChainWorld? world).ShouldBeTrue();
        PhysicsChainWorld activeWorld = world!;
        PhysicsChainColliderShape[] shapes =
        [
            PhysicsChainColliderShape.Sphere(Vector3.Zero, 0.5f),
            PhysicsChainColliderShape.Box(Vector3.One, Vector3.One),
        ];

        PhysicsChainColliderSet first = activeWorld.GetOrCreateColliderSet(shapes);
        PhysicsChainColliderSet duplicate = activeWorld.GetOrCreateColliderSet(shapes.AsSpan());
        PhysicsChainColliderSetCacheSnapshot snapshot = activeWorld.GetColliderSetCacheSnapshot();

        duplicate.ShouldBeSameAs(first);
        snapshot.UniqueSetCount.ShouldBe(1);
        snapshot.LiveSetCount.ShouldBe(1);
        snapshot.TotalShapeCount.ShouldBe(shapes.Length);
        snapshot.EstimatedShapeBytes.ShouldBeGreaterThan(0L);
        snapshot.LookupCount.ShouldBe(2L);
        snapshot.DeduplicatedLookupCount.ShouldBe(1L);
    }

    [Test]
    public void BroadphaseOwnershipNeverIntroducesCrossDomainReadback()
    {
        PhysicsChainColliderBroadphaseDecision cpu = PhysicsChainColliderBroadphasePolicy.Resolve(
            PhysicsChainColliderPoseOwner.Cpu, 16, gpuBroadphaseAvailable: false);
        PhysicsChainColliderBroadphaseDecision gpu = PhysicsChainColliderBroadphasePolicy.Resolve(
            PhysicsChainColliderPoseOwner.Gpu, 16, gpuBroadphaseAvailable: true);
        PhysicsChainColliderBroadphaseDecision unsupportedGpu = PhysicsChainColliderBroadphasePolicy.Resolve(
            PhysicsChainColliderPoseOwner.Gpu, 16, gpuBroadphaseAvailable: false);
        PhysicsChainColliderBroadphaseDecision small = PhysicsChainColliderBroadphasePolicy.Resolve(
            PhysicsChainColliderPoseOwner.Gpu, 4, gpuBroadphaseAvailable: false);

        cpu.Owner.ShouldBe(PhysicsChainColliderBroadphaseOwner.Cpu);
        cpu.IsSupported.ShouldBeTrue();
        gpu.Owner.ShouldBe(PhysicsChainColliderBroadphaseOwner.Gpu);
        gpu.IsSupported.ShouldBeTrue();
        unsupportedGpu.Owner.ShouldBe(PhysicsChainColliderBroadphaseOwner.Gpu);
        unsupportedGpu.IsSupported.ShouldBeFalse();
        small.Owner.ShouldBe(PhysicsChainColliderBroadphaseOwner.None);
        small.IsSupported.ShouldBeTrue();
        cpu.RequiresReadback.ShouldBeFalse();
        gpu.RequiresReadback.ShouldBeFalse();
        unsupportedGpu.RequiresReadback.ShouldBeFalse();
    }

    [Test]
    public void SweptBroadphaseCatchesFastMotionAndTeleportPoseRefits()
    {
        PhysicsChainColliderShape[] shapes = new PhysicsChainColliderShape[8];
        Array.Fill(shapes, PhysicsChainColliderShape.Sphere(Vector3.Zero, 0.5f));
        var runtime = new PhysicsChainColliderRuntimeSet(new PhysicsChainColliderSet(81L, shapes, 82UL));
        for (int index = 0; index < shapes.Length; ++index)
            runtime.TrySetPose(index, Matrix4x4.CreateTranslation(index == 0 ? 50.0f : 1_000.0f + index, 0.0f, 0.0f)).ShouldBeTrue();

        Span<int> candidates = stackalloc int[shapes.Length];
        Span<int> traversal = stackalloc int[runtime.RequiredTraversalStackLength];
        PhysicsChainColliderCandidateQueryResult swept = runtime.QueryCandidates(
            new PhysicsChainAabb(new Vector3(-1.0f), new Vector3(101.0f, 1.0f, 1.0f)),
            candidates,
            traversal);
        swept.Succeeded.ShouldBeTrue();
        candidates[..swept.CandidateCount].Contains(0).ShouldBeTrue();

        runtime.TrySetPose(0, Matrix4x4.CreateTranslation(10_000.0f, 0.0f, 0.0f)).ShouldBeTrue();
        PhysicsChainColliderCandidateQueryResult teleported = runtime.QueryCandidates(
            new PhysicsChainAabb(new Vector3(9_999.0f, -1.0f, -1.0f), new Vector3(10_001.0f, 1.0f, 1.0f)),
            candidates,
            traversal);
        teleported.Succeeded.ShouldBeTrue();
        candidates[..teleported.CandidateCount].Contains(0).ShouldBeTrue();
    }

    [Test]
    public void DegenerateZeroRadiusCoincidentAndLargeCoordinateContactsRemainFinite()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            PhysicsChainColliderShape.Capsule(Vector3.Zero, Vector3.Zero, 0.0f));

        Vector3 coincident = new(2.0f, 3.0f, 4.0f);
        PhysicsChainCpuCollider.Capsule(coincident, coincident, 0.0f)
            .TryCollide(ref coincident, 0.25f).ShouldBeTrue();
        IsFinite(coincident).ShouldBeTrue();
        coincident.ShouldBe(new Vector3(2.0f, 3.25f, 4.0f));

        Vector3 largeCenter = new(1_000_000.0f, -1_000_000.0f, 1_000_000.0f);
        Vector3 largePosition = largeCenter + new Vector3(0.125f, 0.0f, 0.0f);
        PhysicsChainCpuCollider.Sphere(largeCenter, 0.5f)
            .TryCollide(ref largePosition, 0.0f).ShouldBeTrue();
        IsFinite(largePosition).ShouldBeTrue();
        Vector3.Distance(largePosition, largeCenter).ShouldBe(0.5f, 0.0625f);

        Vector3 zeroRadius = largeCenter;
        PhysicsChainCpuCollider.Sphere(largeCenter, 0.0f)
            .TryCollide(ref zeroRadius, 0.0f).ShouldBeFalse();
        zeroRadius.ShouldBe(largeCenter);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
