using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainCpuSharedColliderGroupingTests
{
    [Test]
    public void BatchGroupsInterleavedInstancesByExactSharedPoseSetWithoutAllocation()
    {
        PhysicsChainTemplate template = CreateTemplate();
        PhysicsChainCpuSharedColliderSet firstSet = CreateSharedSet(91L, 92UL, 100.0f);
        PhysicsChainCpuSharedColliderSet secondSet = CreateSharedSet(93L, 94UL, 200.0f);
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle[] handles =
        [
            RegisterShared(backend, template, firstSet),
            RegisterShared(backend, template, secondSet),
            RegisterShared(backend, template, firstSet),
        ];

        backend.TryStepBatch(handles).ShouldBeTrue();
        PhysicsChainCpuBackendSnapshot firstSnapshot = backend.GetSnapshot();
        firstSnapshot.SharedColliderBatchGroupCount.ShouldBe(2L);
        firstSnapshot.SharedColliderGroupedInstanceCount.ShouldBe(3L);
        firstSnapshot.SharedColliderQueryCount.ShouldBe(3L);
        for (int index = 0; index < handles.Length; ++index)
        {
            backend.TryGetInstance(handles[index], out PhysicsChainCpuInstance instance).ShouldBeTrue();
            instance.SimulationFrame.ShouldBe(1L);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 100; ++iteration)
            backend.TryStepBatch(handles).ShouldBeTrue();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0L);
        PhysicsChainCpuBackendSnapshot finalSnapshot = backend.GetSnapshot();
        finalSnapshot.SharedColliderBatchGroupCount.ShouldBe(202L);
        finalSnapshot.SharedColliderGroupedInstanceCount.ShouldBe(303L);
    }

    private static PhysicsChainCpuSharedColliderSet CreateSharedSet(
        long stableId,
        ulong hash,
        float firstColliderX)
    {
        PhysicsChainColliderShape[] shapes = new PhysicsChainColliderShape[8];
        Array.Fill(shapes, PhysicsChainColliderShape.Sphere(Vector3.Zero, 0.25f));
        var shared = new PhysicsChainCpuSharedColliderSet(new PhysicsChainColliderSet(stableId, shapes, hash));
        for (int index = 0; index < shapes.Length; ++index)
            shared.TrySetPose(index, Matrix4x4.CreateTranslation(firstColliderX + index, 0.0f, 0.0f)).ShouldBeTrue();
        shared.RuntimeSet.TrySynchronizeDirtyPoses().ShouldBeTrue();
        return shared;
    }

    private static PhysicsChainArenaHandle RegisterShared(
        PhysicsChainCpuBackend backend,
        PhysicsChainTemplate template,
        PhysicsChainCpuSharedColliderSet shared)
        => backend.RegisterShared(
            template,
            new PhysicsChainCpuInput(1.0f / 60.0f, 1.0f, 1.0f, 1.0f, Vector3.Zero, Vector3.Zero, Vector3.Zero, 0u),
            [new PhysicsChainCpuTreeInput(Vector3.Zero)],
            [
                new PhysicsChainCpuParticleInput(Matrix4x4.Identity),
                new PhysicsChainCpuParticleInput(Matrix4x4.CreateTranslation(Vector3.UnitX)),
            ],
            shared);

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
