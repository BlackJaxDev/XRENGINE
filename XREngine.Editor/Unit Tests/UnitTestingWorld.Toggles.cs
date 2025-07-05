using Assimp;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static Settings Toggles { get; set; } = new();

    public  class Settings
    {
        //Debug visualize
        public bool VisualizeOctree = false;
        public bool VisualizeQuadtree = false;

        //Editor UI
        public bool AddEditorUI = false; //Adds the full editor UI to the camera.
        public bool TransformTool = false; //Adds the transform tool to the scene for testing dragging and rotating etc.
        public bool AllowEditingInVR = true; //Allows the user to edit the scene from desktop in VR.
        public bool VideoStreaming = false; //Adds a video streaming component to the scene for testing video streaming.
        public bool VideoStreamingAudio = false; //Adds a video streaming audio component to the scene for testing video streaming audio.
        public bool RiveUI = true; //Adds a Rive UI component to the scene for testing Rive animations.

        //Misc
        public bool Skybox = true; //Adds a skybox to the scene
        public bool Spline = false; //Adds a 3D spline to the scene.
        public bool DeferredDecal = false; //Adds a deferred decal to the scene.
        public bool AddCameraVRPickup = false; //Adds a camera pickup to the scene for testing VR camera pickup.
        public bool Mirror = true; //Adds a mirror to the scene for testing mirror reflection.

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
        public bool EmulatedVRPawn = true; //Enables an emulated VR pawn for testing without a VR headset. All this does is disallow OpenVR from starting, VRPawn must still be enabled.
        public bool Locomotion = true; //Enables the player to physically locomote in the world. Requires a physical floor.
        public bool ThirdPersonPawn = false; //If on desktop and character pawn is enabled, this will add a third person camera instead of first person.

        //Physics
        public bool PhysicsChain = true; //Adds a jiggle physics chain to the character pawn.
        public bool AddPhysics = true;
        public int PhysicsBallCount = 10; //The number of physics balls to add to the scene.

        //Models
        public bool ImportStaticModel = false; //Imports a scene model to be rendered.
        public bool ImportAnimatedModel = true; //Imports a character model to be animated.
        public float AnimatedModelScale = 1.0f; //The scale of the model when imported.
        public bool AnimatedModelZUp = false; //If true, the model will be rotated 90 degrees around the X axis.

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
        public bool IKTest = false; //Adds an simple IK test tree to the scene.
        public bool TestAnimation = false; //Adds test animations to the character pawn.

        public PostProcessSteps AnimatedModelImportFlags =
            PostProcessSteps.Triangulate |
            //PostProcessSteps.JoinIdenticalVertices |
            //PostProcessSteps.GenerateNormals |
            //PostProcessSteps.CalculateTangentSpace |
            //PostProcessSteps.OptimizeGraph |
            //PostProcessSteps.OptimizeMeshes |
            //PostProcessSteps.SortByPrimitiveType |
            //PostProcessSteps.ImproveCacheLocality |
            PostProcessSteps.GenerateBoundingBoxes |
            //PostProcessSteps.RemoveRedundantMaterials |
            PostProcessSteps.FlipUVs;

        public PostProcessSteps StaticModelImportFlags =
            PostProcessSteps.SplitLargeMeshes |
            //PostProcessSteps.PreTransformVertices |
            PostProcessSteps.Triangulate |
            PostProcessSteps.GenerateNormals |
            PostProcessSteps.CalculateTangentSpace |
            PostProcessSteps.JoinIdenticalVertices |
            PostProcessSteps.OptimizeGraph |
            PostProcessSteps.OptimizeMeshes |
            PostProcessSteps.SortByPrimitiveType |
            PostProcessSteps.ImproveCacheLocality |
            PostProcessSteps.GenerateBoundingBoxes |
            PostProcessSteps.FlipUVs;

        /// <summary>
        /// Indicates if the engine should use shader pipelines to mix and match shader stages.
        /// This will break debug shape rendering, and seems to render slower than making combined programs.
        /// </summary>
        public bool AllowShaderPipelines = false;
        public bool RenderMeshBounds = true;
        public Engine.Rendering.ELoopType RecalcChildMatricesType = Engine.Rendering.ELoopType.Asynchronous;
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

        public string AnimatedModelDesktopPath { get; set; } = "misc\\mitsuki.fbx";
        public float RenderFPS = 0.0f;
        public float UpdateFPS = 60.0f;
        public float FixedFPS = 30.0f;
    }
}