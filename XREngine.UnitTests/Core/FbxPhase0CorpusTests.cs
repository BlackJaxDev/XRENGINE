using NUnit.Framework;
using Shouldly;
using XREngine.Fbx;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class FbxPhase0CorpusTests
{
    [Test]
    public void Phase0SupportMatrix_Targets_ModernBinaryFbx_ForV1()
    {
        FbxPhase0Decisions.VersionMatrix.ShouldContain(x => x.VersionLabel == "7400" && x.Encoding == FbxTransportEncoding.Binary && x.ImportSupport == FbxSupportLevel.TargetedV1 && x.ExportSupport == FbxSupportLevel.TargetedV1);
        FbxPhase0Decisions.VersionMatrix.ShouldContain(x => x.VersionLabel == "7500" && x.Encoding == FbxTransportEncoding.Binary && x.ImportSupport == FbxSupportLevel.TargetedV1 && x.ExportSupport == FbxSupportLevel.TargetedV1);
        FbxPhase0Decisions.VersionMatrix.ShouldContain(x => x.Encoding == FbxTransportEncoding.Ascii && x.ImportSupport == FbxSupportLevel.BestEffort && x.ExportSupport == FbxSupportLevel.Deferred);
    }

    [Test]
    public void Phase0CompressionDecision_Stays_On_BuiltInDotNetDeflate()
    {
        FbxPhase0Decisions.CompressionPolicy.Backend.ShouldBe(FbxCompressionBackend.DotNetZLib);
        FbxPhase0Decisions.CompressionPolicy.RequiresNewDependency.ShouldBeFalse();
    }

    [Test]
    public void Phase0CorpusManifest_Covers_RequiredScenarios_And_CheckedInFiles()
    {
        string workspaceRoot = ResolveWorkspaceRoot();
        string manifestPath = Path.Combine(workspaceRoot, FbxPhase0Decisions.CorpusManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string manifestDirectory = Path.GetDirectoryName(manifestPath).ShouldNotBeNull();
        FbxCorpusManifest manifest = FbxCorpusManifest.Load(manifestPath);

        manifest.SchemaVersion.ShouldBe(1);
        manifest.Entries.Count.ShouldBeGreaterThan(0);

        HashSet<FbxCorpusScenario> scenarios = [];
        foreach (FbxCorpusEntry entry in manifest.Entries)
        {
            foreach (FbxCorpusScenario scenario in entry.Scenarios)
                scenarios.Add(scenario);

            if (entry.Availability is FbxCorpusAvailability.CheckedIn or FbxCorpusAvailability.SyntheticMalformed)
            {
                entry.RelativePath.ShouldNotBeNullOrWhiteSpace($"Manifest entry '{entry.Id}' must point to a checked-in or synthetic fixture.");
                string assetPath = Path.Combine(workspaceRoot, entry.RelativePath!.Replace('/', Path.DirectorySeparatorChar));
                File.Exists(assetPath).ShouldBeTrue($"FBX corpus entry '{entry.Id}' must exist on disk.");
            }

            if (entry.Availability == FbxCorpusAvailability.CheckedIn && entry.ExpectedImportSuccess)
            {
                entry.ExpectedSummaryPath.ShouldNotBeNullOrWhiteSpace($"Checked-in importable asset '{entry.Id}' must have a committed golden summary.");
                string summaryPath = Path.Combine(manifestDirectory, entry.ExpectedSummaryPath!.Replace('/', Path.DirectorySeparatorChar));
                File.Exists(summaryPath).ShouldBeTrue($"Golden summary for '{entry.Id}' should be generated and committed.");
            }
        }

        foreach (FbxCorpusScenario requiredScenario in FbxPhase0Decisions.RequiredCorpusCoverage)
            scenarios.ShouldContain(requiredScenario, $"Phase 0 corpus must cover scenario '{requiredScenario}'.");
    }

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the workspace root for the FBX phase 0 corpus tests.");
    }
}