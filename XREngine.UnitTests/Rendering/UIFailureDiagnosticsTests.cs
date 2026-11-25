using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.UI.State;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIFailureDiagnosticsTests
{
    [Test]
    public void EachReportIsCountedAndRepeatedCodesAreThrottled()
    {
        string code = $"UI.Test.Throttle{Guid.NewGuid():N}";
        long before = UIFailureDiagnostics.ReportedCount;

        UIFailureDiagnostics.Report($"{code}: first description.").ShouldBeTrue();
        UIFailureDiagnostics.Report($"{code}: another description.").ShouldBeFalse();

        (UIFailureDiagnostics.ReportedCount - before).ShouldBe(2);
        UIFailureDiagnostics.LastFailure.ShouldBe($"{code}: another description.");
    }

    [Test]
    public void DifferentCodesLogIndependently()
    {
        string first = $"UI.Test.First{Guid.NewGuid():N}";
        string second = $"UI.Test.Second{Guid.NewGuid():N}";

        UIFailureDiagnostics.Report($"{first}: a.").ShouldBeTrue();
        UIFailureDiagnostics.Report($"{second}: b.").ShouldBeTrue();
    }

    [Test]
    public void EmptyReasonsAreRejected()
        => Should.Throw<ArgumentException>(() => UIFailureDiagnostics.Report(string.Empty));
}
