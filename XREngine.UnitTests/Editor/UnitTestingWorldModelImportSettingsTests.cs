using Assimp;
using NUnit.Framework;
using Shouldly;
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
        options!.FbxBackend.ShouldBe(FbxImportBackend.AssimpLegacy);
        options.GltfBackend.ShouldBe(GltfImportBackend.AssimpLegacy);
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
}