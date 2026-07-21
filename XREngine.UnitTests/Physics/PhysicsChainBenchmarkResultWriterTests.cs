using NUnit.Framework;
using Shouldly;
using XREngine.Editor.Benchmarks.PhysicsChain;

namespace XREngine.UnitTests.Physics;

[TestFixture]
[NonParallelizable]
public sealed class PhysicsChainBenchmarkResultWriterTests
{
    [Test]
    public void ConfiguredWriter_PreservesRawSamplesUnderValidationReportsDirectory()
    {
        string originalDirectory = Environment.CurrentDirectory;
        string? originalRunRoot = Environment.GetEnvironmentVariable(
            PhysicsChainBenchmarkResultWriter.RunRootEnvironmentVariable);
        string repoRoot = Path.Combine(Path.GetTempPath(), $"xrengine-benchmark-{Guid.NewGuid():N}");
        string relativeRunRoot = Path.Combine("Build", "_AgentValidation", "test-run");

        try
        {
            Directory.CreateDirectory(repoRoot);
            Environment.CurrentDirectory = repoRoot;
            Environment.SetEnvironmentVariable(
                PhysicsChainBenchmarkResultWriter.RunRootEnvironmentVariable,
                relativeRunRoot);

            var result = new PhysicsChainBenchmarkResult
            {
                CompletedAt = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
                ScenarioName = "CPU Strict / Linear",
                CopyCount = 100,
                DeterministicSeed = PhysicsChainBenchmarkConfiguration.DefaultDeterministicSeed,
                DebugDisplaysEnabled = false,
                SettleFrameCount = 30,
                MeasurementDurationSeconds = 10.0,
                SpawnMilliseconds = 5.0,
                DestroyMilliseconds = 2.0,
                FrameStatistics = PhysicsChainBenchmarkFrameStatistics.Calculate([1.0, 2.0, 3.0]),
                FrameTimesMilliseconds = [1.0, 2.0, 3.0],
                CpuUploadBytes = 10,
                GpuCopyBytes = 20,
                CpuReadbackBytes = 0,
                DispatchGroupCount = 1,
                DispatchIterationCount = 2,
                ResidentParticleBytes = 4_096,
            };

            PhysicsChainBenchmarkResultWriter.TryWriteConfiguredResult(
                result,
                out string? resultPath,
                out string? error).ShouldBeTrue(error);

            resultPath.ShouldNotBeNull();
            File.Exists(resultPath).ShouldBeTrue();
            string json = File.ReadAllText(resultPath);
            json.ShouldContain("\"FrameTimesMilliseconds\"");
            json.ShouldContain("1");
            json.ShouldContain("2");
            json.ShouldContain("3");
            Path.GetDirectoryName(resultPath).ShouldBe(
                Path.Combine(repoRoot, relativeRunRoot, "reports"));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Environment.SetEnvironmentVariable(
                PhysicsChainBenchmarkResultWriter.RunRootEnvironmentVariable,
                originalRunRoot);
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }
}
