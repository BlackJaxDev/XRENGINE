using NUnit.Framework;
using Shouldly;
using XREngine.Editor.Benchmarks.PhysicsChain;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainBenchmarkHarnessTests
{
    [Test]
    public void MathHarness_UsesSettleAndMeasurementContracts()
    {
        string source = ReadMathHarnessSource();

        source.ShouldContain("ObserveSettleFrame(CaptureBenchmarkSettleSnapshot())");
        source.ShouldContain("BeginBenchmarkMeasurement()");
        source.ShouldContain("ObserveMeasurement(_benchmarkStopwatch.Elapsed.TotalSeconds, _benchmarkFrameCount)");
        source.ShouldContain("PhysicsChainBenchmarkResultWriter.TryWriteConfiguredResult");
    }

    [Test]
    public void MathHarness_ClonesExplicitQualityAndSleepSettings()
    {
        string source = ReadMathHarnessSource();

        source.ShouldContain("target.QualityTier = source.QualityTier;");
        source.ShouldContain("target.EnableAutomaticSleep = source.EnableAutomaticSleep;");
        source.ShouldContain("target.SleepVelocityThreshold = source.SleepVelocityThreshold;");
        source.ShouldContain("target.SleepQuietFrameCount = source.SleepQuietFrameCount;");
    }

    [Test]
    public void ResultWriter_RejectsRootsOutsideAgentValidation()
    {
        string repoRoot = Path.Combine(Path.GetTempPath(), "xrengine-benchmark-writer-contract");
        string outside = Path.Combine(Path.GetTempPath(), "outside-validation-root");

        Should.Throw<ArgumentException>(() =>
            PhysicsChainBenchmarkResultWriter.ResolveReportsDirectory(repoRoot, outside));
    }

    [Test]
    public void ResultWriter_ResolvesReportsUnderConfiguredValidationRun()
    {
        string repoRoot = Path.Combine(Path.GetTempPath(), "xrengine-benchmark-writer-contract");
        string configured = Path.Combine("Build", "_AgentValidation", "run-1");

        string reports = PhysicsChainBenchmarkResultWriter.ResolveReportsDirectory(repoRoot, configured);

        reports.ShouldBe(Path.GetFullPath(Path.Combine(repoRoot, configured, "reports")));
    }

    private static string ReadMathHarnessSource()
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(
            repoRoot,
            "XREngine.Editor",
            "Unit Tests",
            "Math",
            "MathIntersectionsWorldControllerComponent.cs");
        File.Exists(path).ShouldBeTrue($"Expected benchmark harness source '{path}'.");
        return File.ReadAllText(path);
    }

    private static string ResolveRepoRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
