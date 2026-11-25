using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIWorldOutputReadinessTests
{
    [Test]
    public void OrdinaryFrameReadinessRequiresTheExactCurrentOwnerTuple()
    {
        object window = new();
        object pipeline = new();
        object context = new();
        object generation = new();
        UIWorldOutputReadiness? readiness = null;
        (readiness is not null).ShouldBeFalse();

        readiness = new UIWorldOutputReadiness(window, pipeline, context,
            generation, 7, 11, 13, 17);
        readiness.Matches(window, pipeline, context, generation,
            7, 11, 13, 17, hasPendingGeneration: false).ShouldBeTrue();
        readiness.Matches(window, pipeline, context, generation,
            7, 11, 13, 17, hasPendingGeneration: true).ShouldBeFalse();
        readiness.Matches(window, pipeline, new object(), generation,
            7, 11, 13, 17, hasPendingGeneration: false).ShouldBeFalse();
        readiness.Matches(window, pipeline, context, new object(),
            7, 11, 13, 17, hasPendingGeneration: false).ShouldBeFalse();
        readiness.Matches(window, pipeline, context, generation,
            8, 11, 13, 17, hasPendingGeneration: false).ShouldBeFalse();
        readiness.Matches(window, pipeline, context, generation,
            7, 11, 14, 17, hasPendingGeneration: false).ShouldBeFalse();
    }
}
