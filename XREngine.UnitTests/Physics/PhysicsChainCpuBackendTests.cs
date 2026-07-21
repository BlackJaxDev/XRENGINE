using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainCpuBackendTests
{
    [Test]
    public void Register_OwnsTemplateInputsStateAndOutput()
    {
        PhysicsChainTemplate template = CreateLinearTemplate();
        var backend = new PhysicsChainCpuBackend(initialInstanceCapacity: 1);
        PhysicsChainArenaHandle handle = RegisterDefault(backend, template);

        backend.TryGetInstance(handle, out PhysicsChainCpuInstance instance).ShouldBeTrue();
        instance.IsValid.ShouldBeTrue();
        instance.TemplateContentHash.ShouldBe(template.ContentHash);
        instance.TreeCount.ShouldBe(1);
        instance.ParticleCount.ShouldBe(2);
        instance.SimulationFrame.ShouldBe(0L);
        backend.TryGetState(handle, 1, out PhysicsChainCpuState state).ShouldBeTrue();
        backend.TryGetOutput(handle, 1, out PhysicsChainCpuOutput output).ShouldBeTrue();
        state.Position.ShouldBe(Vector3.UnitX);
        output.CurrentPosition.ShouldBe(Vector3.UnitX);

        PhysicsChainCpuBackendSnapshot snapshot = backend.GetSnapshot();
        snapshot.InstanceCapacity.ShouldBe(1);
        snapshot.LiveInstanceCount.ShouldBe(1);
        snapshot.LiveParticleCount.ShouldBe(2);
    }

    [Test]
    public void UpdateAndStep_UseOnlyTheCurrentGenerationalInstance()
    {
        PhysicsChainTemplate template = CreateLinearTemplate();
        var backend = new PhysicsChainCpuBackend(initialInstanceCapacity: 1);
        PhysicsChainArenaHandle stale = RegisterDefault(backend, template);
        backend.Remove(stale).ShouldBeTrue();

        PhysicsChainArenaHandle current = RegisterDefault(backend, template);
        current.Slot.ShouldBe(stale.Slot);
        current.Generation.ShouldNotBe(stale.Generation);
        PhysicsChainCpuInput forceInput = CreateInput() with { ExternalForce = -Vector3.UnitY, DeltaTime = 0.5f };

        backend.TryUpdateInput(stale, forceInput).ShouldBeFalse();
        backend.TryStep(stale).ShouldBeFalse();
        backend.TryGetState(stale, 0, out _).ShouldBeFalse();
        backend.TryUpdateInput(current, forceInput).ShouldBeTrue();
        backend.TryStep(current).ShouldBeTrue();

        backend.TryGetOutput(current, 1, out PhysicsChainCpuOutput output).ShouldBeTrue();
        output.CurrentPosition.Y.ShouldBe(-0.4472136f, 1e-6f);
        backend.TryGetInstance(current, out PhysicsChainCpuInstance instance).ShouldBeTrue();
        instance.SimulationFrame.ShouldBe(1L);
        instance.OutputGeneration.ShouldBe(2u);
    }

    [Test]
    public void InputUpdatesAndReset_AreExplicitAndPreserveTheHandle()
    {
        PhysicsChainTemplate template = CreateLinearTemplate();
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = RegisterDefault(backend, template);
        PhysicsChainCpuParticleInput[] movedInputs =
        [
            new(Matrix4x4.CreateTranslation(new Vector3(5.0f, 0.0f, 0.0f))),
            new(Matrix4x4.CreateTranslation(new Vector3(6.0f, 0.0f, 0.0f))),
        ];

        backend.TryUpdateParticleInputs(handle, movedInputs).ShouldBeTrue();
        backend.TryUpdateTreeInputs(handle, [new(Vector3.UnitY)]).ShouldBeTrue();
        backend.TryReset(handle).ShouldBeTrue();

        backend.IsCurrent(handle).ShouldBeTrue();
        backend.TryGetState(handle, 0, out PhysicsChainCpuState root).ShouldBeTrue();
        backend.TryGetState(handle, 1, out PhysicsChainCpuState child).ShouldBeTrue();
        root.Position.ShouldBe(new Vector3(5.0f, 0.0f, 0.0f));
        child.Position.ShouldBe(new Vector3(6.0f, 0.0f, 0.0f));
        backend.TryGetInstance(handle, out PhysicsChainCpuInstance instance).ShouldBeTrue();
        instance.SimulationFrame.ShouldBe(0L);
        instance.OutputGeneration.ShouldBe(2u);
    }

    [Test]
    public void GeometricInstanceArenaGrowth_PreservesLiveStateAndOutput()
    {
        PhysicsChainTemplate template = CreateLinearTemplate();
        var backend = new PhysicsChainCpuBackend(initialInstanceCapacity: 1);
        PhysicsChainArenaHandle first = RegisterDefault(backend, template);
        backend.TryUpdateInput(first, CreateInput() with { ExternalForce = -Vector3.UnitY }).ShouldBeTrue();
        backend.TryStep(first).ShouldBeTrue();
        backend.TryGetState(first, 1, out PhysicsChainCpuState stateBeforeGrowth).ShouldBeTrue();
        backend.TryGetOutput(first, 1, out PhysicsChainCpuOutput outputBeforeGrowth).ShouldBeTrue();

        _ = RegisterDefault(backend, template);
        _ = RegisterDefault(backend, template);
        _ = RegisterDefault(backend, template);

        PhysicsChainCpuBackendSnapshot snapshot = backend.GetSnapshot();
        snapshot.InstanceCapacity.ShouldBe(4);
        snapshot.InstanceGrowthCount.ShouldBe(2);
        snapshot.LiveInstanceCount.ShouldBe(4);
        snapshot.LiveParticleCount.ShouldBe(8);
        backend.TryGetState(first, 1, out PhysicsChainCpuState stateAfterGrowth).ShouldBeTrue();
        backend.TryGetOutput(first, 1, out PhysicsChainCpuOutput outputAfterGrowth).ShouldBeTrue();
        stateAfterGrowth.ShouldBe(stateBeforeGrowth);
        outputAfterGrowth.ShouldBe(outputBeforeGrowth);
    }

    [Test]
    public void Remove_UpdatesLiveCountsAndRejectsDoubleRemoval()
    {
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = RegisterDefault(backend, CreateLinearTemplate());

        backend.Remove(handle).ShouldBeTrue();
        backend.Remove(handle).ShouldBeFalse();
        backend.GetSnapshot().LiveInstanceCount.ShouldBe(0);
        backend.GetSnapshot().LiveParticleCount.ShouldBe(0);
    }

    [Test]
    public void WarmStep_AllocatesNoManagedMemory()
    {
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = RegisterDefault(backend, CreateLinearTemplate());
        backend.TryStep(handle).ShouldBeTrue();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; ++i)
            backend.TryStep(handle).ShouldBeTrue();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0L);
    }

    [Test]
    public void SharedColliderRegistration_UsesCandidatesWithoutOwnedColliderCopies()
    {
        PhysicsChainColliderShape[] shapes = new PhysicsChainColliderShape[8];
        Array.Fill(shapes, PhysicsChainColliderShape.Sphere(Vector3.Zero, 0.25f));
        var shared = new PhysicsChainCpuSharedColliderSet(new PhysicsChainColliderSet(31L, shapes, 32UL));
        for (int index = 0; index < shapes.Length; ++index)
            shared.TrySetPose(index, Matrix4x4.CreateTranslation(index * 10.0f, 0.0f, 0.0f)).ShouldBeTrue();
        var backend = new PhysicsChainCpuBackend();

        PhysicsChainArenaHandle handle = RegisterShared(backend, CreateLinearTemplate(), shared);
        backend.TryStep(handle).ShouldBeTrue();

        backend.TryGetInstance(handle, out PhysicsChainCpuInstance instance).ShouldBeTrue();
        instance.ColliderCount.ShouldBe(shapes.Length);
        PhysicsChainCpuBackendSnapshot snapshot = backend.GetSnapshot();
        snapshot.LiveColliderCount.ShouldBe(0);
        snapshot.SharedColliderReferenceCount.ShouldBe(1);
        snapshot.SharedColliderQueryCount.ShouldBe(1L);
        snapshot.SharedColliderFullSetFallbackCount.ShouldBe(0L);
        shared.GetSnapshot().CandidateCount.ShouldBeLessThan(shapes.Length);

        backend.Remove(handle).ShouldBeTrue();
        backend.GetSnapshot().SharedColliderReferenceCount.ShouldBe(0);
    }

    [Test]
    public void SharedColliderOverflow_UsesFullSharedSetAndWarmStepAllocatesNothing()
    {
        PhysicsChainColliderShape[] shapes = new PhysicsChainColliderShape[80];
        Array.Fill(shapes, PhysicsChainColliderShape.Sphere(Vector3.Zero, 0.25f));
        var shared = new PhysicsChainCpuSharedColliderSet(new PhysicsChainColliderSet(33L, shapes, 34UL));
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = RegisterShared(backend, CreateLinearTemplate(), shared);
        backend.TryStep(handle).ShouldBeTrue();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int step = 0; step < 100; ++step)
            backend.TryStep(handle).ShouldBeTrue();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0L);
        PhysicsChainCpuBackendSnapshot snapshot = backend.GetSnapshot();
        snapshot.SharedColliderQueryCount.ShouldBe(101L);
        snapshot.SharedColliderFullSetFallbackCount.ShouldBe(101L);
        shared.GetSnapshot().FullSetFallbackCount.ShouldBe(101L);
    }

    [TestCase(0)]
    [TestCase(4)]
    public void SharedColliderZeroAndSmallSets_BypassBroadphase(int colliderCount)
    {
        PhysicsChainColliderShape[] shapes = new PhysicsChainColliderShape[colliderCount];
        Array.Fill(shapes, PhysicsChainColliderShape.Sphere(new Vector3(100.0f), 0.25f));
        var shared = new PhysicsChainCpuSharedColliderSet(new PhysicsChainColliderSet(35L + colliderCount, shapes, 36UL));
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = RegisterShared(backend, CreateLinearTemplate(), shared);

        backend.TryStep(handle).ShouldBeTrue();

        shared.GetSnapshot().SmallSetBypassCount.ShouldBe(1L);
        backend.GetSnapshot().SharedColliderFullSetFallbackCount.ShouldBe(0L);
    }

    [Test]
    public void SharedColliderCandidates_UsePreviousToCurrentSweptBounds()
    {
        PhysicsChainColliderShape[] shapes = new PhysicsChainColliderShape[8];
        Array.Fill(shapes, PhysicsChainColliderShape.Sphere(Vector3.Zero, 0.25f));
        var shared = new PhysicsChainCpuSharedColliderSet(new PhysicsChainColliderSet(41L, shapes, 42UL));
        for (int index = 0; index < shapes.Length; ++index)
            shared.TrySetPose(index, Matrix4x4.CreateTranslation(index == 0 ? 5.0f : 100.0f + index, 0.0f, 0.0f)).ShouldBeTrue();
        PhysicsChainCpuState[] initialStates =
        [
            new() { Position = Vector3.Zero, PreviousPosition = Vector3.Zero },
            new() { Position = Vector3.UnitX * 10.0f, PreviousPosition = Vector3.Zero },
        ];
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = backend.RegisterShared(
            CreateLinearTemplate(), CreateInput(), [new(Vector3.Zero)],
            [new(Matrix4x4.Identity), new(Matrix4x4.CreateTranslation(Vector3.UnitX))],
            shared, initialStates);

        backend.TryStep(handle).ShouldBeTrue();

        shared.GetSnapshot().CandidateCount.ShouldBeGreaterThanOrEqualTo(1L);
        backend.GetSnapshot().SharedColliderFullSetFallbackCount.ShouldBe(0L);
    }

    [Test]
    public void SharedColliderParallelBatchRanges_PublishEveryInstanceAndExactDiagnostics()
    {
        PhysicsChainColliderShape[] shapes = new PhysicsChainColliderShape[8];
        Array.Fill(shapes, PhysicsChainColliderShape.Sphere(Vector3.Zero, 0.25f));
        var shared = new PhysicsChainCpuSharedColliderSet(new PhysicsChainColliderSet(43L, shapes, 44UL));
        for (int colliderIndex = 0; colliderIndex < shapes.Length; ++colliderIndex)
            shared.TrySetPose(colliderIndex, Matrix4x4.CreateTranslation(100.0f + colliderIndex, 0.0f, 0.0f)).ShouldBeTrue();
        shared.RuntimeSet.TrySynchronizeDirtyPoses().ShouldBeTrue();

        const int rangeCount = 4;
        const int handlesPerRange = 32;
        var backend = new PhysicsChainCpuBackend(rangeCount * handlesPerRange);
        var handles = new PhysicsChainArenaHandle[rangeCount * handlesPerRange];
        for (int handleIndex = 0; handleIndex < handles.Length; ++handleIndex)
            handles[handleIndex] = RegisterShared(backend, CreateLinearTemplate(), shared);

        var succeeded = new bool[rangeCount];
        Parallel.For(0, rangeCount, rangeIndex =>
        {
            int start = rangeIndex * handlesPerRange;
            succeeded[rangeIndex] = backend.TryStepBatch(handles.AsSpan(start, handlesPerRange));
        });

        succeeded.ShouldAllBe(static value => value);
        for (int handleIndex = 0; handleIndex < handles.Length; ++handleIndex)
        {
            backend.TryGetInstance(handles[handleIndex], out PhysicsChainCpuInstance instance).ShouldBeTrue();
            instance.SimulationFrame.ShouldBe(1L);
        }
        backend.GetSnapshot().SharedColliderQueryCount.ShouldBe(handles.Length);
        shared.GetSnapshot().QueryCount.ShouldBe(handles.Length);
    }

    private static PhysicsChainArenaHandle RegisterDefault(
        PhysicsChainCpuBackend backend,
        PhysicsChainTemplate template)
        => backend.Register(
            template,
            CreateInput(),
            [new PhysicsChainCpuTreeInput(Vector3.Zero)],
            [
                new PhysicsChainCpuParticleInput(Matrix4x4.Identity),
                new PhysicsChainCpuParticleInput(Matrix4x4.CreateTranslation(Vector3.UnitX)),
            ]);

    private static PhysicsChainArenaHandle RegisterShared(
        PhysicsChainCpuBackend backend,
        PhysicsChainTemplate template,
        PhysicsChainCpuSharedColliderSet shared)
        => backend.RegisterShared(
            template, CreateInput(), [new PhysicsChainCpuTreeInput(Vector3.Zero)],
            [new PhysicsChainCpuParticleInput(Matrix4x4.Identity), new PhysicsChainCpuParticleInput(Matrix4x4.CreateTranslation(Vector3.UnitX))],
            shared);

    private static PhysicsChainCpuInput CreateInput()
        => new(
            DeltaTime: 1.0f,
            Speed: 1.0f,
            ObjectScale: 1.0f,
            Weight: 1.0f,
            Gravity: Vector3.Zero,
            ExternalForce: Vector3.Zero,
            ObjectMove: Vector3.Zero,
            ResetState: 0u);

    private static PhysicsChainTemplate CreateLinearTemplate()
    {
        PhysicsChainTemplateParticle[] particles =
        [
            new(-1, 0, 0, 1, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, Vector3.Zero, Quaternion.Identity),
            new(0, 1, 1, 0, 1.0f, 1.0f, 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, Vector3.UnitX, Quaternion.Identity),
        ];
        return new PhysicsChainTemplate(
            [new PhysicsChainTemplateTree(0, 2, 1, 1.0f)],
            particles,
            [0, 1],
            [new PhysicsChainDepthRange(0, 0, 0, 1), new PhysicsChainDepthRange(0, 1, 1, 1)],
            freezeAxis: 0);
    }
}
