using NUnit.Framework;
using Shouldly;
using XREngine.Gltf;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class GltfPhase0CorpusTests
{
    [Test]
    public void Phase0DecisionMatrix_CommitsToNativeBridgeManagedLayerAndLocalResourceOwnership()
    {
        GltfPhase0Decisions.DependencyMethod.ShouldContain("fastgltf v0.9.0");
        GltfPhase0Decisions.RuntimeStagingRelativePath.ShouldBe("XREngine.Gltf/runtimes/win-x64/native");

        GltfPhase0Decisions.FeatureMatrix.ShouldContain(static feature => feature.Feature == GltfFeatureArea.SceneHierarchy && feature.Support == GltfSupportLevel.Supported);
        GltfPhase0Decisions.FeatureMatrix.ShouldContain(static feature => feature.Feature == GltfFeatureArea.CompatibilityFallback && feature.Support == GltfSupportLevel.Supported);

        GltfPhase0Decisions.ResourceOwnership.NativeLoadsExternalBuffers.ShouldBeTrue();
        GltfPhase0Decisions.ResourceOwnership.ManagedLoadsImages.ShouldBeTrue();
        GltfPhase0Decisions.ResourceOwnership.AllowsRemoteUris.ShouldBeFalse();

        GltfPhase0Decisions.ValidationPolicy.KeepCustomMemoryPoolEnabled.ShouldBeTrue();
        GltfPhase0Decisions.ValidationPolicy.EnableFastGltfValidateInDevelopmentBuilds.ShouldBeFalse();
    }

    [Test]
    public void Phase0ExtensionMatrix_PublishesExplicitSupportAndDiagnostics()
    {
        GltfPhase0Decisions.ExtensionMatrix.ShouldContain(static entry => entry.ExtensionName == "KHR_materials_unlit" && entry.Support == GltfSupportLevel.Supported);
        GltfPhase0Decisions.ExtensionMatrix.ShouldContain(static entry => entry.ExtensionName == "KHR_mesh_quantization" && entry.Support == GltfSupportLevel.Supported);
        GltfPhase0Decisions.ExtensionMatrix.ShouldContain(static entry => entry.ExtensionName == "EXT_meshopt_compression" && entry.Support == GltfSupportLevel.Supported);
        GltfPhase0Decisions.ExtensionMatrix.ShouldContain(static entry => entry.ExtensionName == "KHR_texture_transform" && entry.Support == GltfSupportLevel.Partial);
        GltfPhase0Decisions.ExtensionMatrix.ShouldContain(static entry => entry.ExtensionName == "KHR_texture_basisu" && entry.Support == GltfSupportLevel.UnsupportedWithDiagnostic);
        GltfPhase0Decisions.ExtensionMatrix.ShouldContain(static entry => entry.ExtensionName == "KHR_draco_mesh_compression" && entry.Support == GltfSupportLevel.UnsupportedWithDiagnostic);
    }

    [Test]
    public void Phase0CorpusManifest_Covers_RequiredScenarios_And_CheckedInFiles()
    {
        string workspaceRoot = GltfImportTestUtilities.ResolveWorkspaceRoot();
        string manifestPath = Path.Combine(workspaceRoot, GltfPhase0Decisions.CorpusManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string manifestDirectory = Path.GetDirectoryName(manifestPath).ShouldNotBeNull();
        GltfCorpusManifest manifest = GltfImportTestUtilities.LoadManifest();

        manifest.SchemaVersion.ShouldBe(1);
        manifest.Entries.Count.ShouldBeGreaterThan(0);

        HashSet<GltfCorpusScenario> scenarios = [];
        foreach (GltfCorpusEntry entry in manifest.Entries)
        {
            foreach (GltfCorpusScenario scenario in entry.Scenarios)
                scenarios.Add(scenario);

            if (entry.Availability is GltfCorpusAvailability.CheckedIn or GltfCorpusAvailability.SyntheticMalformed)
            {
                entry.RelativePath.ShouldNotBeNullOrWhiteSpace($"Manifest entry '{entry.Id}' must point to a checked-in or synthetic fixture.");
                string assetPath = Path.Combine(workspaceRoot, entry.RelativePath!.Replace('/', Path.DirectorySeparatorChar));
                File.Exists(assetPath).ShouldBeTrue($"glTF corpus entry '{entry.Id}' must exist on disk.");
            }

            if (entry.Availability == GltfCorpusAvailability.CheckedIn && entry.ExpectedImportSuccess)
            {
                entry.ExpectedSummaryPath.ShouldNotBeNullOrWhiteSpace($"Checked-in importable asset '{entry.Id}' must have a committed golden summary.");
                string summaryPath = Path.Combine(manifestDirectory, entry.ExpectedSummaryPath!.Replace('/', Path.DirectorySeparatorChar));
                File.Exists(summaryPath).ShouldBeTrue($"Golden summary for '{entry.Id}' should be generated and committed.");
            }
        }

        foreach (GltfCorpusScenario requiredScenario in GltfPhase0Decisions.RequiredCorpusCoverage)
            scenarios.ShouldContain(requiredScenario, $"Phase 0 glTF corpus must cover scenario '{requiredScenario}'.");
    }
}