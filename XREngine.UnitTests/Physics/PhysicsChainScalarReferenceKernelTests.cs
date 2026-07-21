using System.Numerics;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainScalarReferenceKernelTests
{
    [Test]
    public void CpuRecords_AreBlittableValueTypes()
    {
        RuntimeHelpers.IsReferenceOrContainsReferences<PhysicsChainCpuInput>().ShouldBeFalse();
        RuntimeHelpers.IsReferenceOrContainsReferences<PhysicsChainCpuTreeInput>().ShouldBeFalse();
        RuntimeHelpers.IsReferenceOrContainsReferences<PhysicsChainCpuParticleInput>().ShouldBeFalse();
        RuntimeHelpers.IsReferenceOrContainsReferences<PhysicsChainCpuState>().ShouldBeFalse();
        RuntimeHelpers.IsReferenceOrContainsReferences<PhysicsChainCpuOutput>().ShouldBeFalse();
    }

    [Test]
    public void LinearChain_AppliesForceThenRestoresSegmentLengthDeterministically()
    {
        PhysicsChainTemplate template = CreateTemplate([-1, 0], [Vector3.Zero, Vector3.UnitX]);
        PhysicsChainCpuParticleInput[] particleInputs = CreateParticleInputs(Vector3.Zero, Vector3.UnitX);
        PhysicsChainCpuState[] firstState = CreateInitialState(Vector3.Zero, Vector3.UnitX);
        PhysicsChainCpuState[] secondState = CreateInitialState(Vector3.Zero, Vector3.UnitX);
        var firstOutput = new PhysicsChainCpuOutput[2];
        var secondOutput = new PhysicsChainCpuOutput[2];
        PhysicsChainCpuInput input = CreateInput(deltaTime: 0.5f, externalForce: -Vector3.UnitY);

        PhysicsChainScalarReferenceKernel.TryStep(template, input, [new(Vector3.Zero)], particleInputs, firstState, firstOutput).ShouldBeTrue();
        PhysicsChainScalarReferenceKernel.TryStep(template, input, [new(Vector3.Zero)], particleInputs, secondState, secondOutput).ShouldBeTrue();

        firstOutput.ShouldBe(secondOutput);
        firstOutput[0].CurrentPosition.ShouldBe(Vector3.Zero);
        firstOutput[1].CurrentPosition.X.ShouldBe(0.8944272f, 1e-6f);
        firstOutput[1].CurrentPosition.Y.ShouldBe(-0.4472136f, 1e-6f);
        Vector3.Distance(firstOutput[0].CurrentPosition, firstOutput[1].CurrentPosition).ShouldBe(1.0f, 1e-6f);
    }

    [Test]
    public void BranchedChain_ProcessesEveryChildAtTheSameDepth()
    {
        PhysicsChainTemplate template = CreateTemplate(
            [-1, 0, 0],
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY]);
        PhysicsChainCpuParticleInput[] particleInputs = CreateParticleInputs(
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitY);
        PhysicsChainCpuState[] states = CreateInitialState(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        var outputs = new PhysicsChainCpuOutput[3];
        PhysicsChainCpuInput input = CreateInput(deltaTime: 1.0f, externalForce: new Vector3(0.0f, 0.0f, 0.25f));

        PhysicsChainScalarReferenceKernel.TryStep(template, input, [new(Vector3.Zero)], particleInputs, states, outputs).ShouldBeTrue();

        Vector3.Distance(outputs[0].CurrentPosition, outputs[1].CurrentPosition).ShouldBe(1.0f, 1e-6f);
        Vector3.Distance(outputs[0].CurrentPosition, outputs[2].CurrentPosition).ShouldBe(1.0f, 1e-6f);
        outputs[1].CurrentPosition.Z.ShouldBe(outputs[2].CurrentPosition.Z, 1e-6f);
        outputs[1].CurrentPosition.X.ShouldBe(outputs[2].CurrentPosition.Y, 1e-6f);
    }

    [Test]
    public void FixedTimestep_ChangesForceIntegrationByTheRequestedStep()
    {
        PhysicsChainTemplate template = CreateTemplate([-1, 0], [Vector3.Zero, Vector3.UnitX]);
        PhysicsChainCpuParticleInput[] particleInputs = CreateParticleInputs(Vector3.Zero, Vector3.UnitX);
        PhysicsChainCpuState[] halfStepState = CreateInitialState(Vector3.Zero, Vector3.UnitX);
        PhysicsChainCpuState[] quarterStepState = CreateInitialState(Vector3.Zero, Vector3.UnitX);
        var halfStepOutput = new PhysicsChainCpuOutput[2];
        var quarterStepOutput = new PhysicsChainCpuOutput[2];

        PhysicsChainScalarReferenceKernel.TryStep(
            template,
            CreateInput(0.5f, -Vector3.UnitY),
            [new(Vector3.Zero)],
            particleInputs,
            halfStepState,
            halfStepOutput).ShouldBeTrue();
        PhysicsChainScalarReferenceKernel.TryStep(
            template,
            CreateInput(0.25f, -Vector3.UnitY),
            [new(Vector3.Zero)],
            particleInputs,
            quarterStepState,
            quarterStepOutput).ShouldBeTrue();

        halfStepOutput[1].CurrentPosition.Y.ShouldBe(-0.4472136f, 1e-6f);
        quarterStepOutput[1].CurrentPosition.Y.ShouldBe(-0.2425356f, 1e-6f);
    }

    [Test]
    public void ResetState_ReinitializesCurrentPreviousAndOutputPositions()
    {
        PhysicsChainTemplate template = CreateTemplate([-1, 0], [Vector3.Zero, Vector3.UnitX]);
        PhysicsChainCpuParticleInput[] particleInputs = CreateParticleInputs(new Vector3(2.0f, 3.0f, 4.0f), new Vector3(3.0f, 3.0f, 4.0f));
        PhysicsChainCpuState[] states =
        [
            new() { Position = new Vector3(99.0f), PreviousPosition = new Vector3(-99.0f) },
            new() { Position = new Vector3(42.0f), PreviousPosition = new Vector3(-42.0f) },
        ];
        var outputs = new PhysicsChainCpuOutput[2];
        PhysicsChainCpuInput input = CreateInput(1.0f, Vector3.Zero) with { ResetState = 1u };

        PhysicsChainScalarReferenceKernel.TryStep(template, input, [new(Vector3.Zero)], particleInputs, states, outputs).ShouldBeTrue();

        for (int i = 0; i < states.Length; ++i)
        {
            Vector3 expected = particleInputs[i].LocalToWorld.Translation;
            states[i].Position.ShouldBe(expected);
            states[i].PreviousPosition.ShouldBe(expected);
            outputs[i].CurrentPosition.ShouldBe(expected);
            outputs[i].PreviousPosition.ShouldBe(expected);
        }
    }

    [Test]
    public void DegenerateZeroLengthSegment_RemainsFiniteAndCollapsesToParent()
    {
        PhysicsChainTemplate template = CreateTemplate([-1, 0], [Vector3.Zero, Vector3.Zero]);
        PhysicsChainCpuParticleInput[] particleInputs = CreateParticleInputs(Vector3.Zero, Vector3.Zero);
        PhysicsChainCpuState[] states = CreateInitialState(Vector3.Zero, Vector3.Zero);
        var outputs = new PhysicsChainCpuOutput[2];

        PhysicsChainScalarReferenceKernel.TryStep(
            template,
            CreateInput(1.0f, Vector3.UnitY),
            [new(Vector3.Zero)],
            particleInputs,
            states,
            outputs).ShouldBeTrue();

        outputs[1].CurrentPosition.ShouldBe(outputs[0].CurrentPosition);
        float.IsFinite(outputs[1].CurrentPosition.X).ShouldBeTrue();
        float.IsFinite(outputs[1].CurrentPosition.Y).ShouldBeTrue();
        float.IsFinite(outputs[1].CurrentPosition.Z).ShouldBeTrue();
    }

    [Test]
    public void InvalidTimestep_RejectsWithoutMutatingStateOrOutput()
    {
        PhysicsChainTemplate template = CreateTemplate([-1, 0], [Vector3.Zero, Vector3.UnitX]);
        PhysicsChainCpuParticleInput[] particleInputs = CreateParticleInputs(Vector3.Zero, Vector3.UnitX);
        PhysicsChainCpuState[] states = CreateInitialState(Vector3.Zero, Vector3.UnitX);
        PhysicsChainCpuState[] before = [.. states];
        PhysicsChainCpuOutput[] outputs = [new() { CurrentPosition = new Vector3(7.0f) }, new() { CurrentPosition = new Vector3(8.0f) }];
        PhysicsChainCpuOutput[] outputBefore = [.. outputs];

        PhysicsChainScalarReferenceKernel.TryStep(
            template,
            CreateInput(float.NaN, Vector3.Zero),
            [new(Vector3.Zero)],
            particleInputs,
            states,
            outputs).ShouldBeFalse();

        states.ShouldBe(before);
        outputs.ShouldBe(outputBefore);
    }

    [Test]
    public void WarmStep_AllocatesNoManagedMemory()
    {
        PhysicsChainTemplate template = CreateTemplate([-1, 0, 1], [Vector3.Zero, Vector3.UnitX, Vector3.UnitX]);
        PhysicsChainCpuParticleInput[] particleInputs = CreateParticleInputs(Vector3.Zero, Vector3.UnitX, new Vector3(2.0f, 0.0f, 0.0f));
        PhysicsChainCpuState[] states = CreateInitialState(Vector3.Zero, Vector3.UnitX, new Vector3(2.0f, 0.0f, 0.0f));
        var outputs = new PhysicsChainCpuOutput[3];
        PhysicsChainCpuTreeInput[] treeInputs = [new(Vector3.Zero)];
        PhysicsChainCpuInput input = CreateInput(1.0f / 60.0f, Vector3.Zero);

        PhysicsChainScalarReferenceKernel.TryStep(template, input, treeInputs, particleInputs, states, outputs).ShouldBeTrue();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; ++i)
            PhysicsChainScalarReferenceKernel.TryStep(template, input, treeInputs, particleInputs, states, outputs).ShouldBeTrue();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0L);
    }

    private static PhysicsChainCpuInput CreateInput(float deltaTime, Vector3 externalForce)
        => new(
            deltaTime,
            Speed: 1.0f,
            ObjectScale: 1.0f,
            Weight: 1.0f,
            Gravity: Vector3.Zero,
            ExternalForce: externalForce,
            ObjectMove: Vector3.Zero,
            ResetState: 0u);

    private static PhysicsChainCpuParticleInput[] CreateParticleInputs(params Vector3[] positions)
    {
        var inputs = new PhysicsChainCpuParticleInput[positions.Length];
        for (int i = 0; i < positions.Length; ++i)
            inputs[i] = new PhysicsChainCpuParticleInput(Matrix4x4.CreateTranslation(positions[i]));
        return inputs;
    }

    private static PhysicsChainCpuState[] CreateInitialState(params Vector3[] positions)
    {
        var states = new PhysicsChainCpuState[positions.Length];
        for (int i = 0; i < positions.Length; ++i)
        {
            states[i].Position = positions[i];
            states[i].PreviousPosition = positions[i];
        }
        return states;
    }

    private static PhysicsChainTemplate CreateTemplate(int[] parents, Vector3[] restOffsets)
    {
        parents.Length.ShouldBe(restOffsets.Length);
        int[] depths = new int[parents.Length];
        int maximumDepth = 0;
        for (int i = 0; i < parents.Length; ++i)
        {
            int parent = parents[i];
            depths[i] = parent < 0 ? 0 : depths[parent] + 1;
            maximumDepth = Math.Max(maximumDepth, depths[i]);
        }

        var particles = new PhysicsChainTemplateParticle[parents.Length];
        for (int i = 0; i < particles.Length; ++i)
        {
            int childCount = 0;
            for (int j = i + 1; j < parents.Length; ++j)
                if (parents[j] == i)
                    ++childCount;

            float segmentLength = parents[i] < 0 ? 0.0f : restOffsets[i].Length();
            particles[i] = new PhysicsChainTemplateParticle(
                parents[i],
                depths[i],
                BoneIndex: i,
                childCount,
                segmentLength,
                segmentLength > 1e-8f ? 1.0f / segmentLength : 0.0f,
                BoneLength: segmentLength,
                Damping: 0.0f,
                Elasticity: 0.0f,
                Stiffness: 0.0f,
                Inert: 0.0f,
                Friction: 0.0f,
                Radius: 0.0f,
                restOffsets[i],
                Quaternion.Identity);
        }

        var ordered = new int[particles.Length];
        var ranges = new PhysicsChainDepthRange[maximumDepth + 1];
        int orderedIndex = 0;
        for (int depth = 0; depth <= maximumDepth; ++depth)
        {
            int rangeStart = orderedIndex;
            for (int particleIndex = 0; particleIndex < particles.Length; ++particleIndex)
                if (depths[particleIndex] == depth)
                    ordered[orderedIndex++] = particleIndex;
            ranges[depth] = new PhysicsChainDepthRange(0, depth, rangeStart, orderedIndex - rangeStart);
        }

        return new PhysicsChainTemplate(
            [new PhysicsChainTemplateTree(0, particles.Length, maximumDepth, particles.Sum(static particle => particle.SegmentLength))],
            particles,
            ordered,
            ranges,
            freezeAxis: 0);
    }
}
