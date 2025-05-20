using Assimp;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static class Toggles
    {
        //Debug visualize
        public const bool VisualizeOctree = false;
        public const bool VisualizeQuadtree = false;

        //Editor UI
        public const bool AddEditorUI = false; //Adds the full editor UI to the camera.
        public const bool TransformTool = true; //Adds the transform tool to the scene for testing dragging and rotating etc.
        public const bool AllowEditingInVR = true; //Allows the user to edit the scene from desktop in VR.
        public const bool VideoStreaming = false; //Adds a video streaming component to the scene for testing video streaming.
        public const bool VideoStreamingAudio = false; //Adds a video streaming audio component to the scene for testing video streaming audio.

        //Misc
        public const bool Skybox = true; //Adds a skybox to the scene
        public const bool Spline = false; //Adds a 3D spline to the scene.
        public const bool DeferredDecal = false; //Adds a deferred decal to the scene.
        public const bool AddCameraVRPickup = false; //Adds a camera pickup to the scene for testing VR camera pickup.
        public const bool Mirror = true; //Adds a mirror to the scene for testing mirror reflection.

        //Light
        public const bool DirLight = true;
        public const bool SpotLight = false;
        public const bool DirLight2 = false;
        public const bool PointLight = false;
        public const bool LightProbe = true; //Adds a test light probe to the scene for PBR lighting.
        public const float LightProbeCaptureMs = 100;
        public static readonly float? StopRealtimeCaptureSec = 5; //5

        //Pawns
        public const bool VRPawn = true; //Enables VR input and pawn.
        public const bool Locomotion = true; //Enables the player to physically locomote in the world. Requires a physical floor.
        public const bool ThirdPersonPawn = false; //If on desktop and character pawn is enabled, this will add a third person camera instead of first person.

        //Physics
        public const bool PhysicsChain = true; //Adds a jiggle physics chain to the character pawn.
        public const bool AddPhysics = true;
        public const int PhysicsBallCount = 10; //The number of physics balls to add to the scene.

        //Models
        public const bool ImportStaticModel = false; //Imports a scene model to be rendered.
        public const bool ImportAnimatedModel = true; //Imports a character model to be animated.
        public const float AnimatedModelScale = 1.0f; //The scale of the model when imported.
        public const bool AnimatedModelZUp = false; //If true, the model will be rotated 90 degrees around the X axis.

        //Audio
        public const bool SoundNode = false;
        public const bool Microphone = false; //Adds a microphone to the scene for testing audio capture.
        public const bool AttachMicToAnimatedModel = true; //If true, the microphone output will be attached to the animated model instead of the flying camera.

        //Face and lip sync
        public const bool VMC = false; //Adds a VMC capture component to the avatar for testing.
        public const bool LipSync = true; //Adds a lip sync component to the avatar for testing.
        public const bool FaceMotion3D = false; //Adds a face motion 3D capture component to the avatar for testing.
        public const bool FaceTracking = false; //Adds a face tracking component to the avatar for testing.

        //Animation
        public const bool AnimationClipVMD = false; //Imports a VMD animation clip for testing.
        public const bool IKTest = false; //Adds an simple IK test tree to the scene.
        public const bool TestAnimation = false; //Adds test animations to the character pawn.

        public static readonly PostProcessSteps AnimatedModelImportFlags =
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

        public static PostProcessSteps StaticModelImportFlags =
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
    }
}