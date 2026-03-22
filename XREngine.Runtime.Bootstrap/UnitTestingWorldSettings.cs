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
    public bool RiveUI = false;
    public bool GPURenderDispatch = false;
    public bool StartInPlayModeWithoutTransitions = false;

    public bool Skybox = true;
    public bool Spline = false;
    public bool DeferredDecal = false;
    public bool AddCameraVRPickup = false;
    public bool Mirror = true;
    public bool ForceDebugOpaquePipeline = false;

    public bool DirLight = true;
    public bool SpotLight = false;
    public bool DirLight2 = false;
    public bool PointLight = false;
    public bool LightProbe = true;
    public float LightProbeCaptureMs = 100;
    public float? StopRealtimeCaptureSec = 5;

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

    public List<ModelImportSettings> ModelsToImport { get; set; } = [];

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

    public float RenderFPS = 0.0f;
    public float UpdateFPS = 60.0f;
    public float FixedFPS = 30.0f;
}