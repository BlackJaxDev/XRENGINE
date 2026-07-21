using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainCpuOutputTests
{
    [Test]
    public void RegistrationAndResetInitializeBothPaletteHistories()
    {
        PhysicsChainTemplate template = CreateTemplate();
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = backend.Register(
            template,
            CreateInput(Vector3.Zero),
            [new PhysicsChainCpuTreeInput(Vector3.Zero)],
            CreateParticleInputs(Vector3.Zero, Vector3.UnitX),
            consumerFlags: PhysicsChainCpuConsumerFlags.Palette | PhysicsChainCpuConsumerFlags.Bounds);
        var current = new Matrix4x4[2];
        var previous = new Matrix4x4[2];

        backend.TryCopyCurrentPalette(handle, current).ShouldBeTrue();
        backend.TryCopyPreviousPalette(handle, previous).ShouldBeTrue();
        previous.ShouldBe(current);

        backend.TryUpdateParticleInputs(handle, CreateParticleInputs(new Vector3(3.0f, 0.0f, 0.0f), new Vector3(4.0f, 0.0f, 0.0f))).ShouldBeTrue();
        backend.TryReset(handle).ShouldBeTrue();
        backend.TryCopyCurrentPalette(handle, current).ShouldBeTrue();
        backend.TryCopyPreviousPalette(handle, previous).ShouldBeTrue();
        previous.ShouldBe(current);
        current[0].Translation.ShouldBe(new Vector3(3.0f, 0.0f, 0.0f));
    }

    [Test]
    public void StepAdvancesPaletteHistoryAndProducesConservativeBounds()
    {
        PhysicsChainTemplate template = CreateTemplate();
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = backend.Register(
            template,
            CreateInput(new Vector3(0.0f, 1.0f, 0.0f)),
            [new PhysicsChainCpuTreeInput(Vector3.Zero)],
            CreateParticleInputs(Vector3.Zero, Vector3.UnitX),
            consumerFlags: PhysicsChainCpuConsumerFlags.Palette | PhysicsChainCpuConsumerFlags.Bounds,
            influenceRadii: [0.25f, 0.5f]);
        var before = new Matrix4x4[2];
        var current = new Matrix4x4[2];
        var previous = new Matrix4x4[2];
        backend.TryCopyCurrentPalette(handle, before).ShouldBeTrue();

        backend.TryStep(handle).ShouldBeTrue();
        backend.TryCopyCurrentPalette(handle, current).ShouldBeTrue();
        backend.TryCopyPreviousPalette(handle, previous).ShouldBeTrue();
        previous.ShouldBe(before);
        current[1].Translation.ShouldNotBe(previous[1].Translation);

        backend.TryGetBounds(handle, out PhysicsChainCpuBounds bounds).ShouldBeTrue();
        bounds.Minimum.X.ShouldBeLessThanOrEqualTo(current[0].Translation.X - 0.25f);
        bounds.Maximum.X.ShouldBeGreaterThanOrEqualTo(current[1].Translation.X + 0.5f);
    }

    [Test]
    public void HoldOutputHistoryCollapsesPreviousPaletteWithoutAdvancingCurrent()
    {
        PhysicsChainTemplate template = CreateTemplate();
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = backend.Register(
            template,
            CreateInput(new Vector3(0.0f, 1.0f, 0.0f)),
            [new PhysicsChainCpuTreeInput(Vector3.Zero)],
            CreateParticleInputs(Vector3.Zero, Vector3.UnitX),
            consumerFlags: PhysicsChainCpuConsumerFlags.Palette);
        var currentBeforeHold = new Matrix4x4[2];
        var currentAfterHold = new Matrix4x4[2];
        var previousAfterHold = new Matrix4x4[2];

        backend.TryStep(handle).ShouldBeTrue();
        backend.TryCopyCurrentPalette(handle, currentBeforeHold).ShouldBeTrue();
        backend.HoldOutputHistory(handle).ShouldBeTrue();
        backend.TryCopyCurrentPalette(handle, currentAfterHold).ShouldBeTrue();
        backend.TryCopyPreviousPalette(handle, previousAfterHold).ShouldBeTrue();

        currentAfterHold.ShouldBe(currentBeforeHold);
        previousAfterHold.ShouldBe(currentAfterHold);
    }

    [Test]
    public void UnrequestedOutputsAreSkippedAndSteadyStepAllocatesNothing()
    {
        PhysicsChainTemplate template = CreateTemplate();
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = backend.Register(
            template,
            CreateInput(Vector3.Zero),
            [new PhysicsChainCpuTreeInput(Vector3.Zero)],
            CreateParticleInputs(Vector3.Zero, Vector3.UnitX));
        Span<Matrix4x4> matrices = stackalloc Matrix4x4[2];
        backend.TryCopyCurrentPalette(handle, matrices).ShouldBeFalse();
        backend.TryCopyPreviousPalette(handle, matrices).ShouldBeFalse();
        backend.TryCopyTransformMirror(handle, matrices).ShouldBeFalse();
        backend.TryGetBounds(handle, out _).ShouldBeFalse();

        backend.TryStep(handle).ShouldBeTrue();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; ++i)
            backend.TryStep(handle).ShouldBeTrue();
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0L);
    }

    [Test]
    public void TransformMirrorIsIndependentFromPaletteConsumer()
    {
        PhysicsChainTemplate template = CreateTemplate();
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = backend.Register(
            template,
            CreateInput(Vector3.Zero),
            [new PhysicsChainCpuTreeInput(Vector3.Zero)],
            CreateParticleInputs(Vector3.Zero, Vector3.UnitX),
            consumerFlags: PhysicsChainCpuConsumerFlags.TransformMirror);
        Span<Matrix4x4> matrices = stackalloc Matrix4x4[2];

        backend.TryCopyTransformMirror(handle, matrices).ShouldBeTrue();
        backend.TryCopyCurrentPalette(handle, matrices).ShouldBeFalse();
    }

    private static PhysicsChainCpuInput CreateInput(Vector3 force)
        => new(1.0f / 60.0f, 1.0f, 1.0f, 1.0f, Vector3.Zero, force, Vector3.Zero, 0u);

    private static PhysicsChainCpuParticleInput[] CreateParticleInputs(Vector3 root, Vector3 child)
        => [new(Matrix4x4.CreateTranslation(root)), new(Matrix4x4.CreateTranslation(child))];

    private static PhysicsChainTemplate CreateTemplate()
        => new(
            [new PhysicsChainTemplateTree(0, 2, 1, 1.0f)],
            [
                new PhysicsChainTemplateParticle(-1, 0, 0, 1, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.1f, Vector3.Zero, Quaternion.Identity),
                new PhysicsChainTemplateParticle(0, 1, 1, 0, 1.0f, 1.0f, 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.1f, Vector3.UnitX, Quaternion.Identity),
            ],
            [0, 1],
            [new PhysicsChainDepthRange(0, 0, 0, 1), new PhysicsChainDepthRange(0, 1, 1, 1)],
            freezeAxis: 0);
}
