using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainCpuColliderTests
{
    [Test]
    public void SphereAndCapsuleMatchCompactSnapshotSemantics()
    {
        Vector3 position = Vector3.Zero;
        PhysicsChainCpuCollider.Sphere(Vector3.Zero, 0.25f).TryCollide(ref position, 0.0f).ShouldBeTrue();
        position.ShouldBe(Vector3.UnitY * 0.25f);

        position = new Vector3(0.5f, 1.0f, 0.0f);
        PhysicsChainCpuCollider.Capsule(Vector3.Zero, new Vector3(0.0f, 2.0f, 0.0f), 1.0f)
            .TryCollide(ref position, 0.0f).ShouldBeTrue();
        position.ShouldBe(new Vector3(1.0f, 1.0f, 0.0f));

        position = Vector3.Zero;
        PhysicsChainCpuCollider.Capsule(Vector3.Zero, Vector3.Zero, 0.25f)
            .TryCollide(ref position, 0.0f).ShouldBeTrue();
        position.ShouldBe(Vector3.UnitY * 0.25f);
    }

    [Test]
    public void BoxAndPlaneMatchInsideAndOutsideSemantics()
    {
        Vector3 position = Vector3.Zero;
        PhysicsChainCpuCollider.Box(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.One)
            .TryCollide(ref position, 0.25f).ShouldBeTrue();
        position.ShouldBe(new Vector3(1.25f, 0.0f, 0.0f));

        position = new Vector3(0.0f, -0.5f, 0.0f);
        PhysicsChainCpuCollider.Plane(Vector3.UnitY, 0.0f, inside: false)
            .TryCollide(ref position, 0.25f).ShouldBeTrue();
        position.ShouldBe(new Vector3(0.0f, 0.25f, 0.0f));

        position = new Vector3(0.0f, 0.5f, 0.0f);
        PhysicsChainCpuCollider.Plane(Vector3.UnitY, 0.0f, inside: true)
            .TryCollide(ref position, 0.25f).ShouldBeTrue();
        position.ShouldBe(new Vector3(0.0f, -0.25f, 0.0f));
    }

    [Test]
    public void InvalidOrNonFiniteColliderDataDoesNotMutatePosition()
    {
        Vector3 original = new(1.0f, 2.0f, 3.0f);
        Vector3 position = original;
        PhysicsChainCpuCollider.Sphere(new Vector3(float.NaN, 0.0f, 0.0f), 1.0f)
            .TryCollide(ref position, 0.0f).ShouldBeFalse();
        position.ShouldBe(original);

        PhysicsChainCpuCollider.Plane(Vector3.Zero, 0.0f, inside: false)
            .TryCollide(ref position, 0.0f).ShouldBeFalse();
        position.ShouldBe(original);
    }

    [Test]
    public void ScalarKernelSpecializesZeroSmallAndGeneralColliderCountsWithoutAllocating()
    {
        PhysicsChainTemplate template = CreateTemplate();
        PhysicsChainCpuInput input = new(1.0f / 60.0f, 1.0f, 1.0f, 1.0f, Vector3.Zero, Vector3.Zero, Vector3.Zero, 0u);
        PhysicsChainCpuTreeInput[] trees = [new(Vector3.Zero)];
        PhysicsChainCpuParticleInput[] particles =
        [
            new(Matrix4x4.CreateTranslation(Vector3.Zero)),
            new(Matrix4x4.CreateTranslation(Vector3.UnitX)),
        ];
        PhysicsChainCpuState[] states = new PhysicsChainCpuState[2];
        PhysicsChainCpuOutput[] outputs = new PhysicsChainCpuOutput[2];
        PhysicsChainCpuCollider plane = PhysicsChainCpuCollider.Plane(Vector3.UnitY, 0.0f, inside: false);

        PhysicsChainScalarReferenceKernel.TryStepNoColliders(
            template, input with { ResetState = 1u }, trees, particles, states, outputs).ShouldBeTrue();
        PhysicsChainScalarReferenceKernel.TryStepSmallColliderSet(
            template, input, trees, particles, [plane], states, outputs).ShouldBeTrue();
        outputs[1].IsColliding.ShouldBe(1u);

        PhysicsChainCpuCollider[] fivePlanes = [plane, plane, plane, plane, plane];
        PhysicsChainScalarReferenceKernel.TryStepSmallColliderSet(
            template, input, trees, particles, fivePlanes, states, outputs).ShouldBeFalse();
        PhysicsChainScalarReferenceKernel.TryStep(
            template, input, trees, particles, fivePlanes, states, outputs).ShouldBeTrue();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; ++i)
            PhysicsChainScalarReferenceKernel.TryStep(template, input, trees, particles, fivePlanes, states, outputs).ShouldBeTrue();
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0L);
    }

    private static PhysicsChainTemplate CreateTemplate()
        => new(
            [new PhysicsChainTemplateTree(0, 2, 1, 1.0f)],
            [
                new PhysicsChainTemplateParticle(-1, 0, 0, 1, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, Vector3.Zero, Quaternion.Identity),
                new PhysicsChainTemplateParticle(0, 1, 1, 0, 1.0f, 1.0f, 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.25f, Vector3.UnitX, Quaternion.Identity),
            ],
            [0, 1],
            [new PhysicsChainDepthRange(0, 0, 0, 1), new PhysicsChainDepthRange(0, 1, 1, 1)],
            freezeAxis: 0);
}
