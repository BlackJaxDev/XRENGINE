using Assimp;
using NUnit.Framework;
using Shouldly;
using System.IO;
using XREngine.Editor;
using XREngine.Rendering.Models;
using XREngine.Runtime.Bootstrap;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class UnitTestingWorldModelImportSettingsTests
{
    [Test]
    public void Settings_RoundTripsFbxLogVerbosity_BetweenEditorAndRuntimeSettings()
    {
        var editorSettings = new EditorUnitTests.Settings
        {
            FbxLogVerbosity = EditorUnitTests.UnitTestFbxLogVerbosity.Verbose,
        };

        UnitTestingWorldSettings runtimeSettings = editorSettings.ToRuntimeSettings();

        runtimeSettings.FbxLogVerbosity.ShouldBe(XREngine.Runtime.Bootstrap.UnitTestFbxLogVerbosity.Verbose);

        runtimeSettings.FbxLogVerbosity = XREngine.Runtime.Bootstrap.UnitTestFbxLogVerbosity.Errors;

        EditorUnitTests.Settings roundTrip = EditorUnitTests.Settings.FromRuntime(runtimeSettings);

        roundTrip.FbxLogVerbosity.ShouldBe(EditorUnitTests.UnitTestFbxLogVerbosity.Errors);
    }

    [Test]
    public void Settings_RoundTripsDynamicDebugLightSettings_BetweenEditorAndRuntimeSettings()
    {
        var editorSettings = new EditorUnitTests.Settings
        {
            DynamicPointLightCount = 7,
            DynamicSpotLightCount = 5,
            DynamicLightsCastShadows = false,
            DynamicLightsForceShadowAtlas = false,
            DynamicLightSeed = 8675309,
        };

        UnitTestingWorldSettings runtimeSettings = editorSettings.ToRuntimeSettings();

        runtimeSettings.DynamicPointLightCount.ShouldBe(7);
        runtimeSettings.DynamicSpotLightCount.ShouldBe(5);
        runtimeSettings.DynamicLightsCastShadows.ShouldBeFalse();
        runtimeSettings.DynamicLightsForceShadowAtlas.ShouldBeFalse();
        runtimeSettings.DynamicLightSeed.ShouldBe(8675309);

        runtimeSettings.DynamicPointLightCount = 2;
        runtimeSettings.DynamicSpotLightCount = 3;
        runtimeSettings.DynamicLightsCastShadows = true;
        runtimeSettings.DynamicLightsForceShadowAtlas = true;
        runtimeSettings.DynamicLightSeed = 42;

        EditorUnitTests.Settings roundTrip = EditorUnitTests.Settings.FromRuntime(runtimeSettings);

        roundTrip.DynamicPointLightCount.ShouldBe(2);
        roundTrip.DynamicSpotLightCount.ShouldBe(3);
        roundTrip.DynamicLightsCastShadows.ShouldBeTrue();
        roundTrip.DynamicLightsForceShadowAtlas.ShouldBeTrue();
        roundTrip.DynamicLightSeed.ShouldBe(42);
    }

    [Test]
    public void ModelImportSettings_RoundTripsImporterBackend_BetweenEditorAndRuntimeSettings()
    {
        var editorSettings = new EditorUnitTests.Settings
        {
            ModelsToImport =
            [
                new EditorUnitTests.Settings.ModelImportSettings
                {
                    Path = "Assets\\Models\\scene.fbx",
                    ImporterBackend = EditorUnitTests.ModelImportBackendPreference.AssimpOnly,
                },
            ],
        };

        UnitTestingWorldSettings runtimeSettings = editorSettings.ToRuntimeSettings();

        runtimeSettings.ModelsToImport.Count.ShouldBe(1);
        runtimeSettings.ModelsToImport[0].ImporterBackend.ShouldBe(XREngine.Runtime.Bootstrap.ModelImportBackendPreference.AssimpOnly);

        runtimeSettings.ModelsToImport[0].ImporterBackend = XREngine.Runtime.Bootstrap.ModelImportBackendPreference.PreferNativeThenAssimp;

        EditorUnitTests.Settings roundTrip = EditorUnitTests.Settings.FromRuntime(runtimeSettings);

        roundTrip.ModelsToImport.Count.ShouldBe(1);
        roundTrip.ModelsToImport[0].ImporterBackend.ShouldBe(EditorUnitTests.ModelImportBackendPreference.PreferNativeThenAssimp);
    }

    [Test]
    public void CreateImportOptions_AssimpOnlyForcesLegacyNativeBackends_AndPreservesImportSettings()
    {
        var model = new EditorUnitTests.Settings.ModelImportSettings
        {
            Kind = EditorUnitTests.UnitTestModelImportKind.Static,
            ImporterBackend = EditorUnitTests.ModelImportBackendPreference.AssimpOnly,
            ImportFlags = PostProcessSteps.FlipUVs | PostProcessSteps.Triangulate,
            Scale = 0.01f,
            ZUp = true,
        };

        ModelImportOptions? options = EditorUnitTests.Models.CreateImportOptions(model, []);

        options.ShouldNotBeNull();
        options!.FbxBackend.ShouldBe(FbxImportBackend.Assimp);
        options.GltfBackend.ShouldBe(GltfImportBackend.Assimp);
        options.PostProcessSteps.ShouldBe(model.ImportFlags);
        options.ScaleConversion.ShouldBe(model.Scale);
        options.ZUp.ShouldBeTrue();
    }

    [Test]
    public void CreateImportOptions_StaticCoacdForcesSplitSubmeshes()
    {
        var model = new EditorUnitTests.Settings.ModelImportSettings
        {
            Kind = EditorUnitTests.UnitTestModelImportKind.Static,
            GenerateCoacdCollidersPerSubmesh = true,
        };

        ModelImportOptions? options = EditorUnitTests.Models.CreateImportOptions(model, []);

        options.ShouldNotBeNull();
        options!.SplitSubmeshesIntoSeparateModelComponents.ShouldBeTrue();
        options.FbxBackend.ShouldBe(FbxImportBackend.Auto);
        options.GltfBackend.ShouldBe(GltfImportBackend.Auto);
    }

    [Test]
    [NonParallelizable]
    public void ResolveModelPath_ResolvesPathsRelativeToCurrentWorkingDirectory()
    {
        string previousCurrentDirectory = Environment.CurrentDirectory;
        string tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            string modelPath = Path.Combine(tempRoot, "Build", "CommonAssets", "Models", "Sponza2", "sponza.obj");
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            File.WriteAllText(modelPath, string.Empty);

            Environment.CurrentDirectory = tempRoot;

            string resolvedPath = EditorUnitTests.Models.ResolveModelPath(
                desktopDir: Path.Combine(tempRoot, "Desktop"),
                rawPath: "Build\\CommonAssets\\Models\\Sponza2\\sponza.obj");

            resolvedPath.ShouldBe(modelPath);
        }
        finally
        {
            Environment.CurrentDirectory = previousCurrentDirectory;

            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
