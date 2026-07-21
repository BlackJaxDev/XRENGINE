using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainCpuRenderOutputTests
{
    [Test]
    public void MultipleConsumersObserveTheSameStablePaletteSlicesAndMotionHistory()
    {
        PhysicsChainTemplate template = CreateTemplate();
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = backend.Register(
            template, CreateInput(new Vector3(0.0f, 1.0f, 0.0f)), [new(Vector3.Zero)], CreateInputs(),
            consumerFlags: PhysicsChainCpuConsumerFlags.Palette | PhysicsChainCpuConsumerFlags.Bounds);

        backend.TryGetRenderOutput(handle, out PhysicsChainCpuRenderOutput rendererA).ShouldBeTrue();
        backend.TryGetRenderOutput(handle, out PhysicsChainCpuRenderOutput rendererB).ShouldBeTrue();
        rendererA.CurrentPalette.Equals(rendererB.CurrentPalette).ShouldBeTrue();
        rendererA.PreviousPalette.Equals(rendererB.PreviousPalette).ShouldBeTrue();
        rendererA.HasValidHistory.ShouldBeTrue();
        Vector3 before = rendererA.CurrentPalette.Span[1].Translation;

        backend.TryStep(handle).ShouldBeTrue();
        backend.TryGetRenderOutput(handle, out PhysicsChainCpuRenderOutput moved).ShouldBeTrue();
        moved.CurrentPalette.Equals(rendererA.CurrentPalette).ShouldBeTrue();
        moved.PreviousPalette.Equals(rendererA.PreviousPalette).ShouldBeTrue();
        moved.PreviousPalette.Span[1].Translation.ShouldBe(before);
        moved.CurrentPalette.Span[1].Translation.ShouldNotBe(before);
        moved.Bounds.IsValid.ShouldBeTrue();

        backend.TryReset(handle).ShouldBeTrue();
        backend.TryGetRenderOutput(handle, out PhysicsChainCpuRenderOutput reset).ShouldBeTrue();
        reset.PreviousPalette.Span.SequenceEqual(reset.CurrentPalette.Span).ShouldBeTrue();
    }

    [Test]
    public void ExplicitTransformMirrorHonorsConfiguredCadenceAndReportsAgeAndCost()
    {
        PhysicsChainTemplate template = CreateTemplate();
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = backend.Register(
            template, CreateInput(new Vector3(0.0f, 1.0f, 0.0f)), [new(Vector3.Zero)], CreateInputs(),
            consumerFlags: PhysicsChainCpuConsumerFlags.TransformMirror,
            mirrorPolicy: new PhysicsChainCpuMirrorPolicy(true, 3));
        var initial = new Matrix4x4[2];
        var mirror = new Matrix4x4[2];
        backend.TryCopyTransformMirror(handle, initial).ShouldBeTrue();

        backend.TryStep(handle).ShouldBeTrue();
        backend.TryCopyTransformMirror(handle, mirror).ShouldBeTrue();
        mirror.ShouldBe(initial);
        backend.TryGetRenderOutput(handle, out PhysicsChainCpuRenderOutput ageOne).ShouldBeTrue();
        ageOne.TransformMirrorAgeFrames.ShouldBe(1);

        backend.TryStep(handle).ShouldBeTrue();
        backend.TryStep(handle).ShouldBeTrue();
        backend.TryCopyTransformMirror(handle, mirror).ShouldBeTrue();
        mirror.ShouldNotBe(initial);
        backend.TryGetRenderOutput(handle, out PhysicsChainCpuRenderOutput refreshed).ShouldBeTrue();
        refreshed.TransformMirrorAgeFrames.ShouldBe(0);
        refreshed.TransformMirrorCostTicks.ShouldBeGreaterThanOrEqualTo(0L);
    }

    [Test]
    public void NoConsumersExposeNoRenderOutputAndAddNoSteadyWorkAllocations()
    {
        PhysicsChainTemplate template = CreateTemplate();
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = backend.Register(
            template, CreateInput(Vector3.Zero), [new(Vector3.Zero)], CreateInputs());
        backend.TryGetRenderOutput(handle, out _).ShouldBeFalse();

        backend.TryStep(handle).ShouldBeTrue();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; ++i)
            backend.TryStep(handle).ShouldBeTrue();
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0L);
    }

    private static PhysicsChainCpuInput CreateInput(Vector3 force)
        => new(1.0f / 60.0f, 1.0f, 1.0f, 1.0f, Vector3.Zero, force, Vector3.Zero, 0u);

    private static PhysicsChainCpuParticleInput[] CreateInputs()
        => [new(Matrix4x4.Identity), new(Matrix4x4.CreateTranslation(Vector3.UnitX))];

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
