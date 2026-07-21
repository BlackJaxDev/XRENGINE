using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainCpuBlittableLayoutTests
{
    [Test]
    public void ActiveSolverStreams_ContainNoManagedReferences()
    {
        RuntimeHelpers.IsReferenceOrContainsReferences<PhysicsChainCpuInput>().ShouldBeFalse();
        RuntimeHelpers.IsReferenceOrContainsReferences<PhysicsChainCpuTreeInput>().ShouldBeFalse();
        RuntimeHelpers.IsReferenceOrContainsReferences<PhysicsChainCpuParticleInput>().ShouldBeFalse();
        RuntimeHelpers.IsReferenceOrContainsReferences<PhysicsChainCpuState>().ShouldBeFalse();
        RuntimeHelpers.IsReferenceOrContainsReferences<PhysicsChainCpuOutput>().ShouldBeFalse();
    }
}
