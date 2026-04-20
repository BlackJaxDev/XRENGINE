using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AmbientOcclusionInputNormalDefaultsTests
{
    [Test]
    public void GtaoDefaultsToInputNormals_WhileHbaoPlusRemainsDepthDerived()
    {
        AmbientOcclusionSettings settings = new();

        settings.GroundTruth.UseInputNormals.ShouldBeTrue();
        settings.HorizonBasedPlus.UseInputNormals.ShouldBeFalse();
        settings.GTAOUseInputNormals.ShouldBeTrue();
        settings.HBAOUseInputNormals.ShouldBeFalse();
    }
}