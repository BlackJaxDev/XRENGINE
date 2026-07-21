using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainOutputTests
{
    [Test]
    public void FirstPalettePublicationInitializesBothHistorySlices()
    {
        var output = new PhysicsChainOutput();
        var first = new PhysicsChainArenaSlice(12, 8, 3u);

        output.AdvancePalette(first, simulationFrame: 10L);

        output.CurrentPalette.ShouldBe(first);
        output.PreviousPalette.ShouldBe(first);
        output.SimulationFrame.ShouldBe(10L);
    }

    [Test]
    public void SubsequentPublicationPreservesPreviousPalette()
    {
        var output = new PhysicsChainOutput();
        var first = new PhysicsChainArenaSlice(0, 4, 1u);
        var second = new PhysicsChainArenaSlice(4, 4, 1u);
        output.AdvancePalette(first, 1L);

        output.AdvancePalette(second, 2L);

        output.CurrentPalette.ShouldBe(second);
        output.PreviousPalette.ShouldBe(first);
    }

    [Test]
    public void ResetHistoryPreventsCrossGenerationMotionData()
    {
        var output = new PhysicsChainOutput();
        output.AdvancePalette(new PhysicsChainArenaSlice(0, 4, 1u), 1L);
        var replacement = new PhysicsChainArenaSlice(16, 4, 2u);
        output.AdvancePalette(replacement, 2L);

        output.ResetHistory();

        output.PreviousPalette.ShouldBe(replacement);
    }

    [Test]
    public void ValidityRequiresInstancePaletteBoundsAndOutputGeneration()
    {
        var output = new PhysicsChainOutput
        {
            InstanceHandle = new PhysicsChainRuntimeHandle(1, 2u),
            CurrentPalette = new PhysicsChainArenaSlice(0, 4, 1u),
            BoundsSlot = new PhysicsChainArenaHandle(3, 1u),
            OutputGeneration = 1u,
            CpuMirrorStatus = PhysicsChainCpuMirrorStatus.Disabled,
            BackendStatus = PhysicsChainBackendStatus.Ready,
        };

        output.IsValid.ShouldBeTrue();
        output.CpuMirrorStatus.ShouldBe(PhysicsChainCpuMirrorStatus.Disabled);
    }
}
