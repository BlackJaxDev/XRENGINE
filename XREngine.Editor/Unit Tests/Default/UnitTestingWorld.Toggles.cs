using Assimp;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using XREngine.Rendering.UI;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    public static Settings Toggles { get; set; } = new();

    public enum UnitTestModelImportKind
    {
        Static,
        Animated,
    }

    public enum StaticModelMaterialMode
    {
        Deferred,
        ForwardPlusTextured,
        ForwardPlusUberShader,
    }

    public enum UnitTestWorldKind
    {
        Default,
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

    public class Settings
    {
        public UnitTestWorldKind WorldKind { get; set; } = UnitTestWorldKind.Default;
        
        //Debug visualize
        public bool VisualizeOctree = false;
        public bool VisualizeQuadtree = false;

        //Editor UI
        public bool AddEditorUI = false; //Adds the full editor UI to the camera.
        public CameraUIDrawMode CameraUIDrawSpaceOnInit { get; set; } = CameraUIDrawMode.Screen; //Controls draw space and offscreen mode for unit testing camera UI.
        public bool TransformTool = false; //Adds the transform tool to the scene for testing dragging and rotating etc.
        public bool AllowEditingInVR = true; //Allows the user to edit the scene from desktop in VR.
        public bool PreviewVRStereoViews = false; //Shows the VR left/right eye render targets side-by-side in a screenspace UI (requires VRPawn).
        public bool VideoStreaming = false; //Adds a video streaming component to the scene for testing video streaming.
        public bool VideoStreamingAudio = false; //Adds a video streaming audio component to the scene for testing video streaming audio.
        public string? VideoStreamingUrl { get; set; } = null; //Stream URL used by the video streaming test component.
        public bool UltralightWebView = false; //Adds an Ultralight web view component to the scene for testing web page rendering.
        public string UltralightWebViewUrl { get; set; } = "https://blackjaxvr.com"; //Page URL used by the Ultralight web view test component.
        public bool DearImGuiUI = true; //Adds a Dear ImGui overlay to the scene for testing immediate-mode UI.
        public bool EnableProfilerLogging = true; //Enables Engine.Profiler frame logging even without Dear ImGui.
        public bool RiveUI = false; //Adds a Rive UI component to the scene for testing Rive animations.
        public bool GPURenderDispatch = false; //Uses GPU render dispatch for rendering instead of CPU culling and issuing draw calls.
        public bool StartInPlayModeWithoutTransitions = false; //Starts in play mode immediately without the edit->play transition.

        //Misc
        public bool Skybox = true; //Adds a skybox to the scene
        public bool Spline = false; //Adds a 3D spline to the scene.
        public bool DeferredDecal = false; //Adds a deferred decal to the scene.
        public bool AddCameraVRPickup = false; //Adds a camera pickup to the scene for testing VR camera pickup.
        public bool Mirror = true; //Adds a mirror to the scene for testing mirror reflection.
        public bool ForceDebugOpaquePipeline = false; //Forces the debug opaque render pipeline instead of the default pipeline.

        //Light
        public bool DirLight = true;
        public bool SpotLight = false;
        public bool DirLight2 = false;
        public bool PointLight = false;
        public bool LightProbe = true; //Adds a test light probe to the scene for PBR lighting.
        public float LightProbeCaptureMs = 100;
        public float? StopRealtimeCaptureSec = 5; //5

        //Pawns
        public bool VRPawn = true; //Enables VR input and pawn.
        public bool UseOpenXR = false; //If true and VRPawn is enabled (and not emulated), initializes VR via OpenXR instead of OpenVR.
        public bool EmulatedVRPawn = true; //Enables an emulated VR pawn for testing without a VR headset. All this does is disallow OpenVR from starting, VRPawn must still be enabled.
        public bool Locomotion = true; //Enables the player to physically locomote in the world. Requires a physical floor.
        public bool ThirdPersonPawn = false; //If on desktop and character pawn is enabled, this will add a third person camera instead of first person.

        /// <summary>
        /// World-space Y translation to apply to the character controller capsule/root on spawn.
        /// If null, UnitTestingWorld will pick a safe default that places the capsule just above the floor.
        /// </summary>
        public float? CharacterControllerCapsuleTranslationY { get; set; }

        //Physics
        public bool PhysicsChain = true; //Adds a jiggle physics chain to the character pawn.
        public bool AddPhysics = true;
        public int PhysicsBallCount = 10; //The number of physics balls to add to the scene.

        //Models
        public StaticModelMaterialMode StaticModelMaterialMode { get; set; } = StaticModelMaterialMode.Deferred;

        public class ModelImportSettings
        {
            public bool Enabled { get; set; } = true;
            public UnitTestModelImportKind Kind { get; set; } = UnitTestModelImportKind.Static;
            public string Path { get; set; } = string.Empty;
            public PostProcessSteps ImportFlags { get; set; } = PostProcessSteps.None;
            public float Scale { get; set; } = 1.0f;
            public bool ZUp { get; set; } = false;

            /// <summary>
            /// Optional additional local-space rotation to apply after import.
            /// Values are degrees.
            /// </summary>
            public YawPitchRollDegrees? YawPitchRoll { get; set; }
        }

        public class YawPitchRollDegrees
        {
            public float Yaw { get; set; } = 0.0f;
            public float Pitch { get; set; } = 0.0f;
            public float Roll { get; set; } = 0.0f;
        }

        public List<ModelImportSettings> ModelsToImport { get; set; } =
        [
            
        ];

        //Audio
        public bool SoundNode = false;
        public bool Microphone = false; //Adds a microphone to the scene for testing audio capture.
        public bool AttachMicToAnimatedModel = true; //If true, the microphone output will be attached to the animated model instead of the flying camera.

        //Face and lip sync
        public bool VMC = false; //Adds a VMC capture component to the avatar for testing.
        public bool LipSync = true; //Adds a lip sync component to the avatar for testing.
        public bool FaceMotion3D = false; //Adds a face motion 3D capture component to the avatar for testing.
        public bool FaceTracking = false; //Adds a face tracking component to the avatar for testing.

        //Animation
        public bool AnimationClipVMD = false; //Imports a VMD animation clip for testing.
        public bool AnimationClipAnim = false; //Imports a .anim clip for testing.
        public string AnimClipPath { get; set; } = "Assets\\Walks\\Basic Walk.anim";
        public bool AnimLooped { get; set; } = true;
        public bool IKTest = false; //Adds an simple IK test tree to the scene.
        public bool TestAnimation = false; //Adds test animations to the character pawn.

        [JsonIgnore]
        public bool HasAnyModelsToImport => ModelsToImport?.Any(m => m?.Enabled ?? false) ?? false;

        [JsonIgnore]
        public bool HasAnimatedModelsToImport => ModelsToImport?.Any(m => (m?.Enabled ?? false) && m.Kind == UnitTestModelImportKind.Animated) ?? false;

        [JsonIgnore]
        public bool HasStaticModelsToImport => ModelsToImport?.Any(m => (m?.Enabled ?? false) && m.Kind == UnitTestModelImportKind.Static) ?? false;

        /// <summary>
        /// Indicates if the engine should use shader pipelines to mix and match shader stages.
        /// This will break debug shape rendering, and seems to render slower than making combined programs.
        /// </summary>
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

        /// <summary>
        /// If true, creates a 1x1x1 box at the origin.
        /// </summary>
        public bool CreateUnitBox { get; set; } = true;

        public float RenderFPS = 0.0f;
        public float UpdateFPS = 60.0f;
        public float FixedFPS = 30.0f;
    }
}
