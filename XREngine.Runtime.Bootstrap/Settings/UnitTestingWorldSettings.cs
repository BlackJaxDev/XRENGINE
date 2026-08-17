using Newtonsoft.Json;
using XREngine.Audio;
using XREngine.Rendering.UI;

namespace XREngine.Runtime.Bootstrap;

public partial class UnitTestingWorldSettings
{
    [JsonIgnore]
    public bool TracksExplicitJsonProperties { get; internal set; }

    [JsonIgnore]
    public IReadOnlySet<string> ExplicitJsonProperties { get; internal set; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public IReadOnlySet<string> ExplicitJsonPropertyPaths { get; internal set; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool IsJsonPropertySpecified(string propertyName)
        => !TracksExplicitJsonProperties || ExplicitJsonProperties.Contains(propertyName);

    public bool IsJsonPropertyPathSpecified(params string[] path)
        => !TracksExplicitJsonProperties || ExplicitJsonPropertyPaths.Contains(string.Join('.', path));

    public UnitTestWorldKind WorldKind { get; set; } = UnitTestWorldKind.Default;

    public bool VisualizeOctree = false;
    public bool VisualizeQuadtree = false;

    public UnitTestEditorType EditorType { get; set; } = UnitTestEditorType.IMGUI;
    public CameraUIDrawMode CameraUIDrawSpaceOnInit { get; set; } = CameraUIDrawMode.Screen;
    public bool TransformTool = false;
    public bool VideoStreaming = false;
    public bool VideoStreamingAudio = false;
    public string? VideoStreamingUrl { get; set; } = null;
    public bool UltralightWebView = false;
    public string UltralightWebViewUrl { get; set; } = "https://blackjaxvr.com";
    public bool EnableProfilerLogging = false;
    public UnitTestFbxLogVerbosity FbxLogVerbosity { get; set; } = UnitTestFbxLogVerbosity.UseEnvironment;
    public UnitTestingRenderSettings Rendering { get; set; } = new();
    public bool RiveUI = false;
    public bool GPURenderDispatch = false;
    public bool StartInPlayModeWithoutTransitions = false;

    public UnitTestingVrSettings VR { get; set; } = new();
    [JsonIgnore]
    public bool AllowEditingInVR = true;
    [JsonIgnore]
    public bool PreviewVRStereoViews = false;

    public bool Skybox = true;
    public bool ProceduralSky = false;
    public bool ProceduralSkyAutoCycle = true;
    public float ProceduralSkyTimeOfDay = 0.25f;
    public bool Spline = false;
    public bool DeferredDecal = false;
    public bool AddCameraVRPickup = false;
    public bool Mirror = true;
    public bool DynamicWaterQuad = false;
    public bool InitializeVolumetricFog = false;
    public bool InitializeAtmosphericScattering = false;
    public bool ForceDebugOpaquePipeline = false;

    public bool DirLight = true;
    public bool DirLightCastsShadows { get; set; } = true;
    public bool SpotLight = false;
    public bool DirLight2 = false;
    public bool PointLight = false;
    public int DynamicPointLightCount { get; set; } = 0;
    public int DynamicSpotLightCount { get; set; } = 0;
    public bool DynamicLightsCastShadows { get; set; } = true;
    public bool DynamicLightsForceShadowAtlas { get; set; } = true;
    public int DynamicLightSeed { get; set; } = 1337;
    public LightProbeMode LightProbe { get; set; } = LightProbeMode.ModelGrid;
    public LightProbeCaptureMode LightProbeCapture { get; set; } = LightProbeCaptureMode.None;
    public float LightProbeCaptureMs = 100;
    public float? StopRealtimeCaptureSec = 5;
    public uint LightProbeResolution { get; set; } = 128;
    public ProbeGridCounts LightProbeGridCounts { get; set; } = new();
    public TranslationXYZ LightProbeGridSpacing { get; set; } = new() { X = 10.0f, Y = 10.0f, Z = 10.0f };
    public TranslationXYZ LightProbeGridCenter { get; set; } = new() { X = 0.0f, Y = 50.0f, Z = 0.0f };
    public TranslationXYZ LightProbeSinglePosition { get; set; } = new() { X = 0.0f, Y = 1.25f, Z = -7.5f };

    [JsonIgnore]
    public bool VRPawn = false;
    [JsonIgnore]
    public bool UseOpenXR = false;
    [JsonIgnore]
    public bool SceneOnlyVRPawn = false;
    public bool Locomotion = true;
    public bool ThirdPersonPawn = false;

    public float? CharacterControllerCapsuleTranslationY { get; set; }

    [JsonProperty("AllowEditingInVR")]
    private bool LegacyAllowEditingInVR
    {
        set => AllowEditingInVR = value;
    }

    [JsonProperty("PreviewVRStereoViews")]
    private bool LegacyPreviewVRStereoViews
    {
        set => PreviewVRStereoViews = value;
    }

    [JsonProperty("VRPawn")]
    private bool LegacyVRPawn
    {
        set => VRPawn = value;
    }

    [JsonProperty("UseOpenXR")]
    private bool LegacyUseOpenXR
    {
        set => UseOpenXR = value;
    }

    [JsonProperty("SceneOnlyVRPawn")]
    private bool LegacySceneOnlyVRPawn
    {
        set => SceneOnlyVRPawn = value;
    }

    public bool PhysicsChain = true;
    public bool AddPhysics = true;
    public int PhysicsBallCount = 10;

    /// <summary>
    /// Startup model imports processed when the Unit Testing World boots. Each array item
    /// is a ModelImportSettings object with Enabled, Kind, MaterialMode, ImporterBackend,
    /// Path, optional UnityProjectRoot, ImportFlags, Scale, ZUp, PostImportFlags, and optional
    /// YawPitchRoll/Translation objects. Unity prefab entries use the Unity converter; recognized
    /// Poiyomi materials are converted to the forward-plus Uber shader.
    /// Paths are relative to the process working directory unless absolute.
    /// </summary>
    public List<ModelImportSettings> ModelsToImport { get; set; } = [];
    /// <summary>
    /// Additional directories that the startup model importer searches recursively by texture file name
    /// when authored texture paths do not resolve relative to the source model.
    /// Relative paths are resolved from the process working directory.
    /// </summary>
    public List<string> TextureLoadDirSearchPaths { get; set; } = [];

    public bool SoundNode = false;
    public bool Microphone = false;
    public bool AttachMicToAnimatedModel = true;
    public bool AudioArchitectureV2 { get; set; } = AudioSettings.AudioArchitectureV2;
    public EAudioTransport AudioTransport { get; set; } = EAudioTransport.OpenAL;
    public EAudioEffects AudioEffects { get; set; } = EAudioEffects.OpenAL_EFX;

    public bool VMC = false;
    public bool LipSync = true;
    public bool FaceMotion3D = false;
    public bool FaceTracking = false;

    public bool AnimationClipVMD = false;
    public bool AnimationClipAnim = false;
    public string AnimClipPath { get; set; } = "Assets\\Walks\\Basic Walk.anim";
    public bool AnimLooped { get; set; } = true;
    public bool HumanoidPoseAuditEnabled = false;
    public string HumanoidPoseAuditOutputPath { get; set; } = "Build\\Logs\\pose_audit\\xrengine_humanoid_pose.json";
    public string? HumanoidPoseAuditReferencePath { get; set; } = null;
    public string? HumanoidPoseAuditComparisonOutputPath { get; set; } = null;
    public int? HumanoidPoseAuditSampleRateOverride { get; set; } = null;
    public bool IKTest = false;
    public bool TestAnimation = false;

    [JsonIgnore]
    public bool HasAnyModelsToImport => ModelsToImport?.Any(m => m?.Enabled ?? false) ?? false;

    [JsonIgnore]
    public bool HasAnimatedModelsToImport => ModelsToImport?.Any(m => (m?.Enabled ?? false) && m.Kind == UnitTestModelImportKind.Animated) ?? false;

    [JsonIgnore]
    public bool HasStaticModelsToImport => ModelsToImport?.Any(m => (m?.Enabled ?? false) && m.Kind == UnitTestModelImportKind.Static) ?? false;

    public bool UseStartupShadowThrottlingForModelImports = true;
    public int StartupMaxShadowTilesRenderedPerFrame { get; set; } = 1;
    public float StartupMaxShadowRenderMilliseconds { get; set; } = 0.5f;

    public bool AllowShaderPipelines = false;
    public bool AllowSkinning { get; set; } = true;
    public EOpenGLShaderLinkStrategy OpenGLShaderLinkStrategy { get; set; } = EOpenGLShaderLinkStrategy.Auto;
    public bool AllowBinaryProgramCaching { get; set; } = true;
    public bool AsyncProgramBinaryUpload { get; set; } = true;
    public bool AsyncProgramCompilation { get; set; } = true;
    public int OpenGLProgramCompileLinkWorkerCount { get; set; } = 1;
    public int MaxAsyncShaderProgramsPerFrame { get; set; } = 16;
    public int OpenGLShaderCompilerThreadCount { get; set; } = -1;
    public bool OpenGLParallelShaderCompileProbeEnabled { get; set; } = true;
    public int OpenGLParallelShaderCompileProbeTimeoutMs { get; set; } = 25;
    public bool RenderMeshBounds = true;

    [JsonIgnore]
    public ERenderLibrary RenderAPI = ERenderLibrary.OpenGL;

    [JsonProperty("RenderAPI")]
    private ERenderLibrary LegacyRenderAPI
    {
        set => RenderAPI = value;
    }

    public EAntiAliasingMode? CameraAntiAliasingModeOverride = null;
    public EPhysicsLibrary PhysicsAPI = EPhysicsLibrary.PhysX;
    public ELoopType RecalcChildMatricesType = ELoopType.Asynchronous;
    public bool TickGroupedItemsInParallel = true;
    public bool SinglePassStereoVR = false;
    public bool RenderPhysicsDebug = false;
    public bool RenderWindowsWhileInVR = true;
    public bool EditorCameraRenderOnDemand = false;
    public bool RenderTransformDebugInfo = true;
    public bool RenderTransformPoints = true;
    public bool RenderTransformCapsules = false;
    public bool RenderTransformLines = true;

    public bool BackgroundShader = false;
    public bool AddCharacterIK = false;
    public bool CreateUnitBox { get; set; } = true;

    /// <summary>
    /// Number of unit-box renderables to create. Values greater than one are
    /// intended for deterministic submission-scaling benchmarks.
    /// </summary>
    public int UnitBoxCount { get; set; } = 1;

    /// <summary>
    /// Number of distinct materials shared across the unit boxes.
    /// </summary>
    public int UnitBoxMaterialCount { get; set; } = 1;

    /// <summary>
    /// Uses the deferred lit-color material for unit boxes. The default keeps
    /// the existing forward unlit unit-box behavior.
    /// </summary>
    public bool UnitBoxDeferredMaterial { get; set; } = false;

    public VolumetricFogVolumeInitSettings VolumetricFog { get; set; } = new();

    public AtmosphericScatteringInitSettings AtmosphericScattering { get; set; } = new();

    public EVSyncMode? VSyncOverride = EVSyncMode.Off;
    public float RenderFPS = 0.0f;
    public float UpdateFPS = 60.0f;
    public float FixedFPS = 30.0f;
}
