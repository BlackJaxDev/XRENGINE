using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AmbientOcclusionInputNormalDefaultsTests
{
    [Test]
    public void DetailSensitiveAmbientOcclusionModes_DefaultToDepthDerivedNormals()
    {
        AmbientOcclusionSettings settings = new();

        settings.GroundTruth.UseInputNormals.ShouldBeFalse();
        settings.HorizonBasedPlus.UseInputNormals.ShouldBeFalse();
        settings.GTAOUseInputNormals.ShouldBeFalse();
        settings.HBAOUseInputNormals.ShouldBeFalse();
    }
}