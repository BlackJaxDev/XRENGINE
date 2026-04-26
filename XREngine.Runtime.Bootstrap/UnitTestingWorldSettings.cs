using Assimp;
using Newtonsoft.Json;
using XREngine.Audio;
using XREngine.Rendering.UI;

namespace XREngine.Runtime.Bootstrap;

public enum UnitTestModelImportKind
{
    Static,
    Animated,
}

public enum ModelImportMaterialMode
{
    Deferred,
    Forward,
    Uber,
}

public enum ModelImportBackendPreference
{
    PreferNativeThenAssimp,
    AssimpOnly,
}

public enum UnitTestFbxLogVerbosity
{
    UseEnvironment,
    Off,
    Errors,
    Warnings,
    Info,
    Verbose,
}

public enum UnitTestWorldKind
{
    Default,
    AudioTesting,
    MathIntersections,
    MeshEditing,
    UberShader,
    PhysxTesting,
    NetworkingPose,
}

public enum CameraUIDrawMode
{
    Screen,
    World,
    Camera,
    WorldOffscreen,
    CameraOffscreen,
}

public enum UnitTestEditorType
{
    None,
    Native,
    IMGUI,
}

public enum LightProbeMode
{
    Off,
    Single,
    Grid,
    ModelGrid,
}

public enum LightProbeCaptureMode
{
    None,
    Startup,
    Realtime,
}

public class UnitTestingWorldSettings
{
    public UnitTestWorldKind WorldKind { get; set; } = UnitTestWorldKind.Default;

    public bool VisualizeOctree = false;
    public bool VisualizeQuadtree = false;

    public UnitTestEditorType EditorType { get; set; } = UnitTestEditorType.IMGUI;
    public CameraUIDrawMode CameraUIDrawSpaceOnInit { get; set; } = CameraUIDrawMode.Screen;
    public bool TransformTool = false;
    public bool AllowEditingInVR = true;
    public bool PreviewVRStereoViews = false;
    public bool VideoStreaming = false;
    public bool VideoStreamingAudio = false;
    public string? VideoStreamingUrl { get; set; } = null;
    public bool UltralightWebView = false;
    public string UltralightWebViewUrl { get; set; } = "https://blackjaxvr.com";
    public bool EnableProfilerLogging = true;
    public UnitTestFbxLogVerbosity FbxLogVerbosity { get; set; } = UnitTestFbxLogVerbosity.UseEnvironment;
    public bool RiveUI = false;
    public bool GPURenderDispatch = false;
    public bool StartInPlayModeWithoutTransitions = false;

    public bool Skybox = true;
    public bool ProceduralSky = false;
    public bool Spline = false;
    public bool DeferredDecal = false;
    public bool AddCameraVRPickup = false;
    public bool Mirror = true;
    public bool DynamicWaterQuad = false;
    public bool InitializeVolumetricFog = false;
    public bool ForceDebugOpaquePipeline = false;

    public bool DirLight = true;
    public bool SpotLight = false;
    public bool DirLight2 = false;
    public bool PointLight = false;
    public LightProbeMode LightProbe { get; set; } = LightProbeMode.ModelGrid;
    public LightProbeCaptureMode LightProbeCapture { get; set; } = LightProbeCaptureMode.None;
    public float LightProbeCaptureMs = 100;
    public float? StopRealtimeCaptureSec = 5;
    public uint LightProbeResolution { get; set; } = 128;
    public ProbeGridCounts LightProbeGridCounts { get; set; } = new();
    public TranslationXYZ LightProbeGridSpacing { get; set; } = new() { X = 10.0f, Y = 10.0f, Z = 10.0f };
    public TranslationXYZ LightProbeGridCenter { get; set; } = new() { X = 0.0f, Y = 50.0f, Z = 0.0f };
    public TranslationXYZ LightProbeSinglePosition { get; set; } = new() { X = 0.0f, Y = 1.25f, Z = -7.5f };

    public bool VRPawn = true;
    public bool UseOpenXR = false;
    public bool EmulatedVRPawn = true;
    public bool Locomotion = true;
    public bool ThirdPersonPawn = false;

    public float? CharacterControllerCapsuleTranslationY { get; set; }

    public bool PhysicsChain = true;
    public bool AddPhysics = true;
    public int PhysicsBallCount = 10;

    public class ModelImportSettings
    {
        public bool Enabled { get; set; } = true;
        public UnitTestModelImportKind Kind { get; set; } = UnitTestModelImportKind.Static;
        public ModelImportMaterialMode MaterialMode { get; set; } = ModelImportMaterialMode.Deferred;
        /// <summary>
        /// When true and <see cref="MaterialMode"/> is <see cref="ModelImportMaterialMode.Deferred"/>,
        /// materials whose textures have alpha channels will use forward (lit) shaders
        /// instead of deferred shaders, giving proper transparency blending for those meshes.
        /// Materials without alpha stay in the deferred pipeline.
        /// </summary>
        public bool UseForwardForTransparent { get; set; } = false;
        /// <summary>
        /// Selects how this model import chooses between native format-specific importers
        /// and Assimp fallback. PreferNativeThenAssimp uses a native importer when the
        /// format has one available and falls back to Assimp otherwise. Today the native
        /// path exists for FBX and glTF.
        /// </summary>
        public ModelImportBackendPreference ImporterBackend { get; set; } = ModelImportBackendPreference.PreferNativeThenAssimp;
        public string Path { get; set; } = string.Empty;
        public PostProcessSteps ImportFlags { get; set; } = PostProcessSteps.None;
        public float Scale { get; set; } = 1.0f;
        public bool ZUp { get; set; } = false;
        public bool GenerateCoacdCollidersPerSubmesh { get; set; } = false;
        /// <summary>
        /// When true, each imported submesh gets its own ModelComponent instead of
        /// grouping all submeshes from the same source node into one ModelComponent.
        /// Increases GPU resource usage; leave false unless per-submesh scene nodes are needed.
        /// </summary>
        public bool SplitSubmeshesIntoSeparateModelComponents { get; set; } = false;
        public YawPitchRollDegrees? YawPitchRoll { get; set; }
        public TranslationXYZ? Translation { get; set; }
    }

    public class YawPitchRollDegrees
    {
        public float Yaw { get; set; } = 0.0f;
        public float Pitch { get; set; } = 0.0f;
        public float Roll { get; set; } = 0.0f;
    }

    public class TranslationXYZ
    {
        public float X { get; set; } = 0.0f;
        public float Y { get; set; } = 0.0f;
        public float Z { get; set; } = 0.0f;
    }

    public class ColorRgb
    {
        public float R { get; set; } = 1.0f;
        public float G { get; set; } = 1.0f;
        public float B { get; set; } = 1.0f;
    }

    public class ProbeGridCounts
    {
        public int X { get; set; } = 10;
        public int Y { get; set; } = 1;
        public int Z { get; set; } = 10;
    }

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

    public bool AllowShaderPipelines = false;
    public bool RenderMeshBounds = true;

    public ERenderLibrary RenderAPI = ERenderLibrary.OpenGL;
    public EPhysicsLibrary PhysicsAPI = EPhysicsLibrary.PhysX;
    public ELoopType RecalcChildMatricesType = ELoopType.Asynchronous;
    public bool TickGroupedItemsInParallel = true;
    public bool SinglePassStereoVR = false;
    public bool RenderPhysicsDebug = false;
    public bool RenderWindowsWhileInVR = true;
    public bool RenderTransformDebugInfo = true;
    public bool RenderTransformPoints = true;
    public bool RenderTransformCapsules = false;
    public bool RenderTransformLines = true;

    public bool BackgroundShader = false;
    public bool AddCharacterIK = false;
    public bool CreateUnitBox { get; set; } = true;

    public class VolumetricFogVolumeInitSettings
    {
        public TranslationXYZ Translation { get; set; } = new() { X = 0.0f, Y = 6.0f, Z = 0.0f };
        public TranslationXYZ HalfExtents { get; set; } = new() { X = 24.0f, Y = 8.0f, Z = 24.0f };
        public ColorRgb ScatteringColor { get; set; } = new() { R = 0.86f, G = 0.9f, B = 1.0f };
        public float Density { get; set; } = 0.025f;
        public float NoiseScale { get; set; } = 0.08f;
        public TranslationXYZ NoiseVelocity { get; set; } = new() { X = 0.0f, Y = 0.03f, Z = 0.01f };
        public float NoiseThreshold { get; set; } = 0.35f;
        public float NoiseAmount { get; set; } = 0.5f;
        public float EdgeFade { get; set; } = 4.0f;
        public float Anisotropy { get; set; } = 0.2f;
        public float LightContribution { get; set; } = 1.0f;
        public int Priority { get; set; } = 0;
        public float Intensity { get; set; } = 1.0f;
        public float MaxDistance { get; set; } = 150.0f;
        public float StepSize { get; set; } = 4.0f;
        public float JitterStrength { get; set; } = 0.25f;
    }

    public VolumetricFogVolumeInitSettings VolumetricFog { get; set; } = new();

    public float RenderFPS = 0.0f;
    public float UpdateFPS = 60.0f;
    public float FixedFPS = 30.0f;
}
