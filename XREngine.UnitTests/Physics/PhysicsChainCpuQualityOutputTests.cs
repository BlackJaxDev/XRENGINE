using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainCpuQualityOutputTests
{
    [Test]
    public void PaletteBoundsAndTransformMirrorCadencesAreIndependent()
    {
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = backend.Register(
            CreateTemplate(),
            CreateInput(new Vector3(0.0f, 1.0f, 0.0f)),
            [new PhysicsChainCpuTreeInput(Vector3.Zero)],
            CreateParticleInputs(),
            consumerFlags: PhysicsChainCpuConsumerFlags.Palette
                | PhysicsChainCpuConsumerFlags.Bounds
                | PhysicsChainCpuConsumerFlags.TransformMirror,
            mirrorPolicy: PhysicsChainCpuMirrorPolicy.EveryFrame,
            influenceRadii: [0.25f, 0.5f]);
        var paletteBefore = new Matrix4x4[2];
        var paletteAfter = new Matrix4x4[2];
        var mirrorBefore = new Matrix4x4[2];
        var mirrorAfter = new Matrix4x4[2];
        backend.TryCopyCurrentPalette(handle, paletteBefore).ShouldBeTrue();
        backend.TryCopyTransformMirror(handle, mirrorBefore).ShouldBeTrue();
        backend.TryGetBounds(handle, out PhysicsChainCpuBounds boundsBefore).ShouldBeTrue();

        backend.TryUpdateOutputPolicy(handle, new PhysicsChainCpuOutputPolicy(
            PhysicsChainOutputCadence.Hold,
            PhysicsChainOutputCadence.EverySimulationStep,
            PhysicsChainOutputCadence.EverySimulationStep)).ShouldBeTrue();
        backend.TryStep(handle).ShouldBeTrue();

        backend.TryCopyCurrentPalette(handle, paletteAfter).ShouldBeTrue();
        backend.TryCopyTransformMirror(handle, mirrorAfter).ShouldBeTrue();
        backend.TryGetBounds(handle, out PhysicsChainCpuBounds boundsAfter).ShouldBeTrue();
        paletteAfter.ShouldBe(paletteBefore);
        mirrorAfter[1].Translation.ShouldNotBe(mirrorBefore[1].Translation);
        boundsAfter.ShouldNotBe(boundsBefore);
    }

    [Test]
    public void InterpolatedPaletteReadDoesNotAdvanceSimulationOrOutputGeneration()
    {
        var backend = new PhysicsChainCpuBackend();
        PhysicsChainArenaHandle handle = backend.Register(
            CreateTemplate(),
            CreateInput(new Vector3(0.0f, 1.0f, 0.0f)),
            [new PhysicsChainCpuTreeInput(Vector3.Zero)],
            CreateParticleInputs(),
            consumerFlags: PhysicsChainCpuConsumerFlags.Palette);
        backend.TryStep(handle).ShouldBeTrue();
        backend.TryGetInstance(handle, out PhysicsChainCpuInstance before).ShouldBeTrue();
        var previous = new Matrix4x4[2];
        var current = new Matrix4x4[2];
        var interpolated = new Matrix4x4[2];
        backend.TryCopyPreviousPalette(handle, previous).ShouldBeTrue();
        backend.TryCopyCurrentPalette(handle, current).ShouldBeTrue();

        backend.TryCopyInterpolatedPalette(handle, interpolated, 0.5f).ShouldBeTrue();

        backend.TryGetInstance(handle, out PhysicsChainCpuInstance after).ShouldBeTrue();
        after.SimulationFrame.ShouldBe(before.SimulationFrame);
        after.OutputGeneration.ShouldBe(before.OutputGeneration);
        Vector3.Distance(
            interpolated[1].Translation,
            Vector3.Lerp(previous[1].Translation, current[1].Translation, 0.5f))
            .ShouldBeLessThan(0.00001f);
    }

    [Test]
    public void CollisionPolicyDisablesProjectionWithoutRemovingColliderInputs()
    {
        PhysicsChainTemplate template = CreateTemplate();
        PhysicsChainCpuTreeInput[] trees = [new(Vector3.Zero)];
        PhysicsChainCpuParticleInput[] particles = CreateParticleInputs();
        PhysicsChainCpuCollider[] colliders = [PhysicsChainCpuCollider.Plane(Vector3.UnitY, 0.0f, inside: false)];
        var disabledStates = new PhysicsChainCpuState[2];
        var disabledOutputs = new PhysicsChainCpuOutput[2];
        var enabledStates = new PhysicsChainCpuState[2];
        var enabledOutputs = new PhysicsChainCpuOutput[2];
        PhysicsChainCpuInput reset = CreateInput(Vector3.Zero) with { ResetState = 1u };
        PhysicsChainScalarReferenceKernel.TryStep(
            template, reset, trees, particles, colliders, disabledStates, disabledOutputs).ShouldBeTrue();
        PhysicsChainScalarReferenceKernel.TryStep(
            template, reset, trees, particles, colliders, enabledStates, enabledOutputs).ShouldBeTrue();

        PhysicsChainScalarReferenceKernel.TryStep(
            template,
            reset with { ResetState = 0u, CollisionEnabled = 0u },
            trees,
            particles,
            colliders,
            disabledStates,
            disabledOutputs).ShouldBeTrue();
        PhysicsChainScalarReferenceKernel.TryStep(
            template,
            reset with { ResetState = 0u, CollisionEnabled = 1u },
            trees,
            particles,
            colliders,
            enabledStates,
            enabledOutputs).ShouldBeTrue();

        disabledOutputs[1].IsColliding.ShouldBe(0u);
        enabledOutputs[1].IsColliding.ShouldBe(1u);
    }

    private static PhysicsChainCpuInput CreateInput(Vector3 force)
        => new(1.0f / 60.0f, 1.0f, 1.0f, 1.0f, Vector3.Zero, force, Vector3.Zero, 0u);

    private static PhysicsChainCpuParticleInput[] CreateParticleInputs()
        =>
        [
            new PhysicsChainCpuParticleInput(Matrix4x4.CreateTranslation(Vector3.Zero)),
            new PhysicsChainCpuParticleInput(Matrix4x4.CreateTranslation(Vector3.UnitX)),
        ];

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
