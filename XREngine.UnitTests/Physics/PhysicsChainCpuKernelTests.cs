using System.Numerics;
using System.Runtime.Intrinsics.X86;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainCpuKernelTests
{
    [Test]
    public void EightCompatibleLinearChainsMatchScalarAndReportSelectedFamily()
    {
        PhysicsChainTemplate template = CreateLinearTemplate(segmentLength: 1.0f);
        var batched = new PhysicsChainCpuBackend();
        var scalar = new PhysicsChainCpuBackend();
        Span<PhysicsChainArenaHandle> batchHandles = stackalloc PhysicsChainArenaHandle[8];
        Span<PhysicsChainArenaHandle> scalarHandles = stackalloc PhysicsChainArenaHandle[8];
        for (int lane = 0; lane < 8; ++lane)
        {
            Vector3 origin = new(100_000.0f + lane * 4.0f, lane * 0.25f, -lane);
            PhysicsChainCpuInput input = CreateInput(new Vector3(0.1f * lane, -0.25f, 0.05f), new Vector3(0.001f * lane));
            PhysicsChainCpuParticleInput[] particles = CreateParticleInputs(origin, origin + Vector3.UnitX);
            batchHandles[lane] = batched.Register(template, input, [new(Vector3.Zero)], particles);
            scalarHandles[lane] = scalar.Register(template, input, [new(Vector3.Zero)], particles);
        }

        batched.TryStepBatch(batchHandles).ShouldBeTrue();
        for (int lane = 0; lane < 8; ++lane)
        {
            scalar.TryStep(scalarHandles[lane]).ShouldBeTrue();
            batched.TryGetOutput(batchHandles[lane], 1, out PhysicsChainCpuOutput actual).ShouldBeTrue();
            scalar.TryGetOutput(scalarHandles[lane], 1, out PhysicsChainCpuOutput expected).ShouldBeTrue();
            Vector3.Distance(actual.CurrentPosition, expected.CurrentPosition).ShouldBeLessThan(0.02f);
            batched.TryGetInstance(batchHandles[lane], out PhysicsChainCpuInstance instance).ShouldBeTrue();
            instance.KernelFamily.ShouldBe(Avx2.IsSupported
                ? PhysicsChainCpuKernelFamily.Avx2LinearBatch
                : PhysicsChainCpuKernelFamily.ScalarLinear);
        }
    }

    [Test]
    public void ColliderAndBranchedCasesSelectExplicitScalarFamilies()
    {
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainTemplate linear = CreateLinearTemplate(1.0f);
        PhysicsChainArenaHandle colliderHandle = backend.Register(
            linear, CreateInput(Vector3.Zero, Vector3.Zero), [new(Vector3.Zero)],
            CreateParticleInputs(Vector3.Zero, Vector3.UnitX),
            colliders: [PhysicsChainCpuCollider.Plane(Vector3.UnitY, 0.0f, inside: false)]);
        backend.TryStep(colliderHandle).ShouldBeTrue();
        backend.TryGetInstance(colliderHandle, out PhysicsChainCpuInstance colliderInstance).ShouldBeTrue();
        colliderInstance.KernelFamily.ShouldBe(PhysicsChainCpuKernelFamily.ScalarLinear);

        PhysicsChainTemplate branched = CreateBranchedTemplate();
        PhysicsChainArenaHandle branchedHandle = backend.Register(
            branched, CreateInput(Vector3.Zero, Vector3.Zero), [new(Vector3.Zero)],
            CreateParticleInputs(Vector3.Zero, Vector3.UnitX, Vector3.UnitY));
        backend.TryStep(branchedHandle).ShouldBeTrue();
        backend.TryGetInstance(branchedHandle, out PhysicsChainCpuInstance branchedInstance).ShouldBeTrue();
        branchedInstance.KernelFamily.ShouldBe(PhysicsChainCpuKernelFamily.DepthOrderedBranched);
    }

    [Test]
    public void BatchRejectsNonFiniteInputWithoutPublishingNonFiniteState()
    {
        PhysicsChainTemplate template = CreateLinearTemplate(1.0f);
        var backend = new PhysicsChainCpuBackend();
        Span<PhysicsChainArenaHandle> handles = stackalloc PhysicsChainArenaHandle[8];
        for (int lane = 0; lane < 8; ++lane)
            handles[lane] = backend.Register(template, CreateInput(Vector3.Zero, Vector3.Zero), [new(Vector3.Zero)], CreateParticleInputs(Vector3.Zero, Vector3.UnitX));

        backend.TryUpdateInput(handles[3], CreateInput(new Vector3(float.NaN, 0.0f, 0.0f), Vector3.Zero)).ShouldBeTrue();
        backend.TryStepBatch(handles).ShouldBeFalse();
        backend.TryGetState(handles[3], 1, out PhysicsChainCpuState state).ShouldBeTrue();
        float.IsFinite(state.Position.X).ShouldBeTrue();
        float.IsFinite(state.Position.Y).ShouldBeTrue();
        float.IsFinite(state.Position.Z).ShouldBeTrue();
    }

    [Test]
    public void DegenerateBatchTailAndAvxPathAllocateNothing()
    {
        PhysicsChainTemplate template = CreateLinearTemplate(segmentLength: 0.0f);
        var backend = new PhysicsChainCpuBackend();
        Span<PhysicsChainArenaHandle> handles = stackalloc PhysicsChainArenaHandle[9];
        for (int lane = 0; lane < handles.Length; ++lane)
            handles[lane] = backend.Register(template, CreateInput(Vector3.Zero, Vector3.Zero), [new(Vector3.Zero)], CreateParticleInputs(Vector3.Zero, Vector3.Zero));
        backend.TryStepBatch(handles).ShouldBeTrue();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; ++iteration)
            backend.TryStepBatch(handles).ShouldBeTrue();
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0L);
    }

    private static PhysicsChainCpuInput CreateInput(Vector3 force, Vector3 objectMove)
        => new(1.0f / 60.0f, 1.0f, 1.0f, 1.0f, Vector3.Zero, force, objectMove, 0u);

    private static PhysicsChainCpuParticleInput[] CreateParticleInputs(params Vector3[] positions)
    {
        var result = new PhysicsChainCpuParticleInput[positions.Length];
        for (int i = 0; i < positions.Length; ++i)
            result[i] = new PhysicsChainCpuParticleInput(Matrix4x4.CreateTranslation(positions[i]));
        return result;
    }

    private static PhysicsChainTemplate CreateLinearTemplate(float segmentLength)
        => new(
            [new PhysicsChainTemplateTree(0, 2, 1, segmentLength)],
            [Particle(-1, 0, 1, 0.0f, Vector3.Zero), Particle(0, 1, 0, segmentLength, Vector3.UnitX * segmentLength)],
            [0, 1],
            [new PhysicsChainDepthRange(0, 0, 0, 1), new PhysicsChainDepthRange(0, 1, 1, 1)],
            freezeAxis: 0);

    private static PhysicsChainTemplate CreateBranchedTemplate()
        => new(
            [new PhysicsChainTemplateTree(0, 3, 1, 2.0f)],
            [Particle(-1, 0, 2, 0.0f, Vector3.Zero), Particle(0, 1, 0, 1.0f, Vector3.UnitX), Particle(0, 1, 0, 1.0f, Vector3.UnitY)],
            [0, 1, 2],
            [new PhysicsChainDepthRange(0, 0, 0, 1), new PhysicsChainDepthRange(0, 1, 1, 2)],
            freezeAxis: 0);

    private static PhysicsChainTemplateParticle Particle(int parent, int depth, int children, float length, Vector3 rest)
        => new(parent, depth, depth, children, length, length > 1e-8f ? 1.0f / length : 0.0f, length,
            Damping: 0.1f, Elasticity: 0.1f, Stiffness: 0.1f, Inert: 0.1f, Friction: 0.1f, Radius: 0.1f,
            rest, Quaternion.Identity);
}
