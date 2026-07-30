using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedAttributeReconstructionTemporalFlagsTests
{
    [TestCase(EAdvancedVelocityValidityReason.Valid, false)]
    [TestCase(EAdvancedVelocityValidityReason.NewlyVisible, true)]
    [TestCase(EAdvancedVelocityValidityReason.Teleported, true)]
    [TestCase(EAdvancedVelocityValidityReason.TopologyChanged, true)]
    [TestCase(EAdvancedVelocityValidityReason.VertexCountChanged, true)]
    [TestCase(EAdvancedVelocityValidityReason.HistoryReset, true)]
    [TestCase(EAdvancedVelocityValidityReason.ArenaOverflow, true)]
    [TestCase(EAdvancedVelocityValidityReason.FrameGap, true)]
    public void DrawFlags_RoundTripVelocityReasonAndReactiveState(
        EAdvancedVelocityValidityReason reason,
        bool expectedReactive)
    {
        const uint featureBits = 0x155u;
        uint flags =
            AdvancedReconstructionTemporalFlags.PackVelocityReason(
                featureBits,
                reason);

        (flags & 0xFFFFu).ShouldBe(featureBits);
        AdvancedReconstructionTemporalFlags.DecodeVelocityReason(flags)
            .ShouldBe(reason);
        AdvancedReconstructionTemporalFlags.IsReactive(flags)
            .ShouldBe(expectedReactive);
    }

    [Test]
    public void DrawFlags_RejectVelocityReasonsThatDoNotFitTheAbi()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            static () =>
                AdvancedReconstructionTemporalFlags.PackVelocityReason(
                    0u,
                    (EAdvancedVelocityValidityReason)16u));
    }
}
