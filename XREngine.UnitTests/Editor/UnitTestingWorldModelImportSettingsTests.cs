using Assimp;
using Newtonsoft.Json;
using NUnit.Framework;
using Shouldly;
using System.IO;
using System.Linq;
using XREngine.Components.Scene.Mesh;
using XREngine.Editor;
using XREngine.Rendering.Models;
using XREngine.Runtime.Bootstrap;
using XREngine.Runtime.Bootstrap.Builders;

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
    public void ParseJsonc_TracksOnlyTopLevelPropertiesPresentInJsonc()
    {
        const string json = """
        {
          // Only these two values are intended unit-test overrides.
          "RenderAPI": "OpenGL",
          "VolumetricFog": {
            "Density": 0.5
          }
        }
        """;

        UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(json);

        settings.TracksExplicitJsonProperties.ShouldBeTrue();
        settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderAPI)).ShouldBeTrue();
        settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.VolumetricFog)).ShouldBeTrue();
        settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.PhysicsAPI)).ShouldBeFalse();
        settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.UpdateFPS)).ShouldBeFalse();
    }

    [Test]
    public void ApplyStartupOverrides_PreservesExistingSettingsForOmittedJsoncProperties()
    {
        const string json = """
        {
          "RenderAPI": "OpenGL",
          "RenderFPS": 144.0,
          "AudioTransport": "NAudio"
        }
        """;

        UnitTestingWorldSettings unitTestSettings = UnitTestingWorldSettingsStore.ParseJsonc(json);
        var startupSettings = new GameStartupSettings
        {
            GPURenderDispatch = true,
            TargetUpdatesPerSecond = 72.0f,
            TargetFramesPerSecond = 30.0f,
            FixedFramesPerSecond = 72.0f,
            AudioEffectsOverride = new(EAudioEffects.SteamAudio, true),
            DefaultUserSettings = new UserSettings
            {
                RenderLibrary = ERenderLibrary.Vulkan,
                PhysicsLibrary = EPhysicsLibrary.Jolt,
                VSync = EVSyncMode.Adaptive,
            },
        };

        UnitTestingWorldSettingsStore.ApplyStartupOverrides(startupSettings, unitTestSettings);

        startupSettings.DefaultUserSettings.RenderLibrary.ShouldBe(ERenderLibrary.OpenGL);
        startupSettings.DefaultUserSettings.PhysicsLibrary.ShouldBe(EPhysicsLibrary.Jolt);
        startupSettings.DefaultUserSettings.VSync.ShouldBe(EVSyncMode.Adaptive);
        startupSettings.VSyncOverride.HasOverride.ShouldBeFalse();
        startupSettings.GPURenderDispatch.ShouldBeTrue();
        startupSettings.TargetUpdatesPerSecond.ShouldBe(72.0f);
        startupSettings.TargetFramesPerSecond.ShouldBe(144.0f);
        startupSettings.FixedFramesPerSecond.ShouldBe(72.0f);
        startupSettings.AudioTransportOverride.HasOverride.ShouldBeTrue();
        startupSettings.AudioTransportOverride.Value.ShouldBe(EAudioTransport.NAudio);
        startupSettings.AudioEffectsOverride.HasOverride.ShouldBeTrue();
        startupSettings.AudioEffectsOverride.Value.ShouldBe(EAudioEffects.SteamAudio);
        startupSettings.AudioArchitectureV2Override.HasOverride.ShouldBeFalse();
    }

    [Test]
    public void ParseJsonc_LegacyRenderApiNormalizesToGroupedRenderingForWriteback()
    {
        const string json = """
        {
          "RenderAPI": "Vulkan"
        }
        """;

        UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(json);

        settings.RenderAPI.ShouldBe(ERenderLibrary.Vulkan);
        settings.Rendering.RenderBackend.ShouldBe(ERenderLibrary.Vulkan);
        settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderAPI)).ShouldBeTrue();
        settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.Rendering)).ShouldBeFalse();

        string serialized = JsonConvert.SerializeObject(settings);
        serialized.ShouldContain("\"Rendering\"");
        serialized.ShouldContain("\"RenderBackend\":1");
        serialized.ShouldNotContain("\"RenderAPI\"");
    }

    [Test]
    public void ApplyStartupOverrides_AppliesExplicitVSyncOverride()
    {
        const string json = """
        {
          "VSyncOverride": "Off"
        }
        """;

        UnitTestingWorldSettings unitTestSettings = UnitTestingWorldSettingsStore.ParseJsonc(json);
        var startupSettings = new GameStartupSettings
        {
            DefaultUserSettings = new UserSettings
            {
                VSync = EVSyncMode.Adaptive,
            },
        };

        UnitTestingWorldSettingsStore.ApplyStartupOverrides(startupSettings, unitTestSettings);

        startupSettings.DefaultUserSettings.VSync.ShouldBe(EVSyncMode.Off);
        startupSettings.VSyncOverride.HasOverride.ShouldBeTrue();
        startupSettings.VSyncOverride.Value.ShouldBe(EVSyncMode.Off);
    }

    [Test]
    public void Settings_RoundTripsEditorCameraRenderOnDemand_BetweenEditorAndRuntimeSettings()
    {
        var editorSettings = new EditorUnitTests.Settings
        {
            EditorCameraRenderOnDemand = true,
        };

        UnitTestingWorldSettings runtimeSettings = editorSettings.ToRuntimeSettings();

        runtimeSettings.EditorCameraRenderOnDemand.ShouldBeTrue();

        const string json = """
        {
          "EditorCameraRenderOnDemand": true
        }
        """;

        UnitTestingWorldSettings parsed = UnitTestingWorldSettingsStore.ParseJsonc(json);

        parsed.EditorCameraRenderOnDemand.ShouldBeTrue();
        parsed.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.EditorCameraRenderOnDemand)).ShouldBeTrue();
    }

    [Test]
    public void BootstrapPawnFactory_AppliesEditorCameraRenderOnDemandThroughInputBridge()
    {
        string pawnFactory = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/BootstrapPawnFactory.cs").Replace("\r\n", "\n");
        string inputBridge = ReadWorkspaceFile("XREngine.Runtime.InputIntegration/BootstrapFlyableCameraFactory.cs").Replace("\r\n", "\n");
        string editorBridge = ReadWorkspaceFile("XREngine.Editor/Bootstrap/BootstrapEditorHookRegistration.cs").Replace("\r\n", "\n");
        string editorPawn = ReadWorkspaceFile("XREngine.Editor/EditorFlyingCameraPawnComponent.cs").Replace("\r\n", "\n");

        pawnFactory.ShouldContain("settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.EditorCameraRenderOnDemand))");
        pawnFactory.ShouldContain("BootstrapInputBridge.Current?.SetFlyableCameraRenderOnDemand(pawnComp, settings.EditorCameraRenderOnDemand);");
        inputBridge.ShouldContain("void SetFlyableCameraRenderOnDemand(XRComponent pawn, bool enabled);");
        editorBridge.ShouldContain("public void SetFlyableCameraRenderOnDemand(XRComponent pawn, bool enabled)");
        editorBridge.ShouldContain("editorPawn.RenderOnDemand = enabled;");
        editorPawn.ShouldContain("case nameof(RenderOnDemand):");
        editorPawn.ShouldContain("private const int RenderOnDemandSettleFrameCount = 3;");
        editorPawn.ShouldContain("_renderOnDemandSettleFramesRemaining = Math.Max(_renderOnDemandSettleFramesRemaining, RenderOnDemandSettleFrameCount);");
        editorPawn.ShouldContain("|| _renderOnDemandSettleFramesRemaining > 0");
        editorPawn.ShouldContain("if (_renderOnDemand)\n                    InvalidateView();");
        editorPawn.ShouldContain("vp.Suppress3DSceneRendering = false;");
        editorPawn.ShouldContain("vp.SuppressAutoExposureUpdates = _renderOnDemand && vp.Suppress3DSceneRendering;");
        editorPawn.ShouldContain("vp.SuppressAutoExposureUpdates = false;");
        editorPawn.ShouldNotContain("HoldAutoExposureForCameraMotion");
        editorPawn.ShouldNotContain("HoldActiveCameraAutoExposureForMotion");
        editorPawn.ShouldNotContain("EditorCameraExposureHoldFrameCount");
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
    public void Settings_RoundTripsProceduralSkySettings_BetweenEditorAndRuntimeSettings()
    {
        var editorSettings = new EditorUnitTests.Settings
        {
            ProceduralSky = true,
            ProceduralSkyAutoCycle = false,
            ProceduralSkyTimeOfDay = 0.6f,
        };

        UnitTestingWorldSettings runtimeSettings = editorSettings.ToRuntimeSettings();

        runtimeSettings.ProceduralSky.ShouldBeTrue();
        runtimeSettings.ProceduralSkyAutoCycle.ShouldBeFalse();
        runtimeSettings.ProceduralSkyTimeOfDay.ShouldBe(0.6f);

        runtimeSettings.ProceduralSkyAutoCycle = true;
        runtimeSettings.ProceduralSkyTimeOfDay = 0.25f;

        EditorUnitTests.Settings roundTrip = EditorUnitTests.Settings.FromRuntime(runtimeSettings);

        roundTrip.ProceduralSky.ShouldBeTrue();
        roundTrip.ProceduralSkyAutoCycle.ShouldBeTrue();
        roundTrip.ProceduralSkyTimeOfDay.ShouldBe(0.25f);
    }

    [Test]
    public void ParseJsonc_ReadsProceduralSkySettings()
    {
        const string json = """
        {
          "ProceduralSky": true,
          "ProceduralSkyAutoCycle": false,
          "ProceduralSkyTimeOfDay": 0.6
        }
        """;

        UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(json);

        settings.ProceduralSky.ShouldBeTrue();
        settings.ProceduralSkyAutoCycle.ShouldBeFalse();
        settings.ProceduralSkyTimeOfDay.ShouldBe(0.6f);
        settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.ProceduralSkyAutoCycle)).ShouldBeTrue();
        settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.ProceduralSkyTimeOfDay)).ShouldBeTrue();
    }

    [Test]
    public void BootstrapWorldFactory_AppliesProceduralSkySettingsToSkybox()
    {
        UnitTestingWorldSettings previousSettings = RuntimeBootstrapState.Settings;
        try
        {
            RuntimeBootstrapState.Settings = new UnitTestingWorldSettings
            {
                Skybox = true,
                ProceduralSky = true,
                ProceduralSkyAutoCycle = false,
                ProceduralSkyTimeOfDay = 0.6f,
                DirLight = false,
                SpotLight = false,
                PointLight = false,
                LightProbe = LightProbeMode.Off,
                VRPawn = false,
                Locomotion = false,
            };

            var world = BootstrapWorldFactory.CreateUnitTestWorld(setUI: false, isServer: false);

            SkyboxComponent? skybox = world.Scenes
                .SelectMany(scene => scene.RootNodes)
                .SelectMany(root => root.FindAllDescendantComponents<SkyboxComponent>())
                .SingleOrDefault();

            skybox.ShouldNotBeNull();
            skybox.Mode.ShouldBe(ESkyboxMode.DynamicProcedural);
            skybox.AutoCycle.ShouldBeFalse();
            skybox.TimeOfDay.ShouldBe(0.6f);
        }
        finally
        {
            RuntimeBootstrapState.Settings = previousSettings;
        }
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
            PostImportFlags = EditorUnitTests.ModelPostImportFlags.GenerateCoacdCollidersPerSubmesh,
        };

        ModelImportOptions? options = EditorUnitTests.Models.CreateImportOptions(model, []);

        options.ShouldNotBeNull();
        options!.SplitSubmeshesIntoSeparateModelComponents.ShouldBeTrue();
        options.FbxBackend.ShouldBe(FbxImportBackend.Auto);
        options.GltfBackend.ShouldBe(GltfImportBackend.Auto);
    }

    [Test]
    public void CreateImportOptions_SeparateMeshIslands_PassesThroughForAnimatedImports()
    {
        var model = new EditorUnitTests.Settings.ModelImportSettings
        {
            Kind = EditorUnitTests.UnitTestModelImportKind.Animated,
            PostImportFlags = EditorUnitTests.ModelPostImportFlags.SeparateMeshIslands,
        };

        ModelImportOptions? options = EditorUnitTests.Models.CreateImportOptions(model, []);

        options.ShouldNotBeNull();
        options!.SeparateMeshIslands.ShouldBeTrue();
        options.SplitSubmeshesIntoSeparateModelComponents.ShouldBeFalse();
    }

    [Test]
    public void CreateImportOptions_SpatialOcclusionPartitionUsesTunedTriangleLimit()
    {
        var model = new EditorUnitTests.Settings.ModelImportSettings
        {
            Kind = EditorUnitTests.UnitTestModelImportKind.Static,
            PostImportFlags = EditorUnitTests.ModelPostImportFlags.SpatiallyPartitionMeshesForOcclusion,
        };

        ModelImportOptions? options = EditorUnitTests.Models.CreateImportOptions(model, []);

        options.ShouldNotBeNull();
        options!.SpatialPartitionMaxTriangles.ShouldBe(4096);
        options.SeparateMeshIslands.ShouldBeFalse();
    }

    [Test]
    public void CreateImportOptions_GenerateIndividualSceneNodesPerSubmesh_ImpliesSplitSubmeshes()
    {
        var model = new EditorUnitTests.Settings.ModelImportSettings
        {
            Kind = EditorUnitTests.UnitTestModelImportKind.Animated,
            PostImportFlags = EditorUnitTests.ModelPostImportFlags.GenerateIndividualSceneNodesPerSubmesh,
        };

        ModelImportOptions? options = EditorUnitTests.Models.CreateImportOptions(model, []);

        options.ShouldNotBeNull();
        options!.GenerateSceneNodesPerSubmesh.ShouldBeTrue();
        options.SplitSubmeshesIntoSeparateModelComponents.ShouldBeTrue();
    }

    [Test]
    public void ModelImportSettings_RoundTripsPostImportFlags_BetweenEditorAndRuntimeSettings()
    {
        var editorSettings = new EditorUnitTests.Settings
        {
            ModelsToImport =
            [
                new EditorUnitTests.Settings.ModelImportSettings
                {
                    Path = "Assets\\Models\\scene.fbx",
                    PostImportFlags =
                        EditorUnitTests.ModelPostImportFlags.GenerateCoacdCollidersPerSubmesh |
                        EditorUnitTests.ModelPostImportFlags.GenerateIndividualSceneNodesPerSubmesh |
                        EditorUnitTests.ModelPostImportFlags.PutAllCoacdCollidersIntoOneStaticRigidBodyComponent |
                        EditorUnitTests.ModelPostImportFlags.SpatiallyPartitionMeshesForOcclusion,
                },
            ],
        };

        UnitTestingWorldSettings runtimeSettings = editorSettings.ToRuntimeSettings();

        runtimeSettings.ModelsToImport.Count.ShouldBe(1);
        runtimeSettings.ModelsToImport[0].PostImportFlags.ShouldBe(
            XREngine.Runtime.Bootstrap.ModelPostImportFlags.GenerateCoacdCollidersPerSubmesh |
            XREngine.Runtime.Bootstrap.ModelPostImportFlags.GenerateIndividualSceneNodesPerSubmesh |
            XREngine.Runtime.Bootstrap.ModelPostImportFlags.PutAllCoacdCollidersIntoOneStaticRigidBodyComponent |
            XREngine.Runtime.Bootstrap.ModelPostImportFlags.SpatiallyPartitionMeshesForOcclusion);

        runtimeSettings.ModelsToImport[0].PostImportFlags = XREngine.Runtime.Bootstrap.ModelPostImportFlags.SeparateMeshIslands;

        EditorUnitTests.Settings roundTrip = EditorUnitTests.Settings.FromRuntime(runtimeSettings);

        roundTrip.ModelsToImport.Count.ShouldBe(1);
        roundTrip.ModelsToImport[0].PostImportFlags.ShouldBe(EditorUnitTests.ModelPostImportFlags.SeparateMeshIslands);
    }

    [Test]
    public void ModelImportSettings_LegacyBooleanPostImportSettings_MapToPostImportFlags()
    {
        const string json = """
        {
          "ModelsToImport": [
            {
              "Path": "Assets\\Models\\scene.fbx",
              "GenerateCoacdCollidersPerSubmesh": true,
              "SplitSubmeshesIntoSeparateModelComponents": true,
              "SeparateMeshIslands": true
            }
          ]
        }
        """;

        UnitTestingWorldSettings settings = JsonConvert.DeserializeObject<UnitTestingWorldSettings>(json)!;

        settings.ModelsToImport.Count.ShouldBe(1);
        settings.ModelsToImport[0].PostImportFlags.ShouldBe(
            XREngine.Runtime.Bootstrap.ModelPostImportFlags.GenerateCoacdCollidersPerSubmesh |
            XREngine.Runtime.Bootstrap.ModelPostImportFlags.SplitSubmeshesIntoSeparateModelComponents |
            XREngine.Runtime.Bootstrap.ModelPostImportFlags.SeparateMeshIslands);
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

    private static string ReadWorkspaceFile(string relativePath)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XREngine.slnx")))
                return File.ReadAllText(Path.Combine(directory, relativePath));

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException("Could not locate repository root from test directory.");
    }
}
