using Assimp;
using Extensions;
using MagicPhysX;
using Silk.NET.Input;
using Silk.NET.OpenAL;
using System.Numerics;
using XREngine.Actors.Types;
using XREngine.Animation;
using XREngine.Animation.IK;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Components.Scene;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Transforms;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Components;
using XREngine.Data.Components.Scene;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Editor.UI.Components;
using XREngine.Editor.UI.Toolbar;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Physics.Physx;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Components;
using XREngine.Scene.Components.Animation;
using XREngine.Scene.Components.Physics;
using XREngine.Scene.Components.Scripting;
using XREngine.Scene.Components.VR;
using XREngine.Scene.Transforms;
using static XREngine.Scene.Transforms.RigidBodyTransform;
using Quaternion = System.Numerics.Quaternion;

namespace XREngine.Editor;

public static class EditorWorld
{
    //Unit testing toggles

    //Debug visualize
    public const bool VisualizeOctree = false;
    public const bool VisualizeQuadtree = false;

    //Editor UI
    public const bool AddEditorUI = false; //Adds the full editor UI to the camera.
    public const bool TransformTool = false; //Adds the transform tool to the scene for testing dragging and rotating etc.
    public const bool AllowEditingInVR = false; //Allows the user to edit the scene from desktop in VR.
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
    public const bool VRPawn = false; //Enables VR input and pawn.
    public const bool Locomotion = true; //Enables the player to physically locomote in the world. Requires a physical floor.
    public const bool ThirdPersonPawn = true; //If on desktop and character pawn is enabled, this will add a third person camera instead of first person.

    //Physics
    public const bool PhysicsChain = true; //Adds a jiggle physics chain to the character pawn.
    public const bool Physics = true;
    public const int PhysicsBallCount = 10; //The number of physics balls to add to the scene.

    //Models
    public const bool StaticModel = false; //Imports a scene model to be rendered.
    public const bool AnimatedModel = true; //Imports a character model to be animated.
    public const float ModelScale = 1.0f; //The scale of the model when imported.
    public const bool ModelZUp = false; //If true, the model will be rotated 90 degrees around the X axis.

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

    private static readonly Queue<float> _fpsAvg = new();
    private static void TickFPS(UITextComponent t)
    {
        _fpsAvg.Enqueue(1.0f / Engine.Time.Timer.Render.Delta);
        if (_fpsAvg.Count > 60)
            _fpsAvg.Dequeue();
        string str = $"{MathF.Round(_fpsAvg.Sum() / _fpsAvg.Count)}hz";
        var net = Engine.Networking;
        if (net is not null)
        {
            str += $"\n{net.AverageRoundTripTimeMs}ms";
            str += $"\n{net.DataPerSecondString}";
            str += $"\n{net.PacketsPerSecond}p/s";
        }
        t.Text = str;
    }

    static void OnFinishedImportingAvatar(SceneNode? rootNode)
    {
        if (rootNode is null)
            return;

        //Debug.Out(rootNode.PrintTree());

        var humanComp = rootNode.AddComponent<HumanoidComponent>()!;
        humanComp.SolveIK = false;
        humanComp.LeftArmIKEnabled = false;
        humanComp.RightArmIKEnabled = false;
        humanComp.LeftLegIKEnabled = false;
        humanComp.RightLegIKEnabled = false;
        humanComp.HipToHeadIKEnabled = false;

        var animator = rootNode.AddComponent<AnimStateMachineComponent>()!;

        if (FaceTracking)
        {
            const string vrcftPrefix = "/avatar/parameters/";
            var ftOscReceiver = rootNode.AddComponent<FaceTrackingReceiverComponent>()!;
            ftOscReceiver.ParameterPrefix = vrcftPrefix;
            ftOscReceiver.GenerateARKitStateMachine();

            var ftOscSender = rootNode.AddComponent<OscSenderComponent>()!;
            ftOscSender.ParameterPrefix = vrcftPrefix;

            animator!.StateMachine.VariableChanged += ftOscSender.StateMachineVariableChanged;
        }

        if (!VRPawn)
        {
            var humanik = rootNode.AddComponent<HumanoidIKSolverComponent>()!;

            humanik.SetIKPositionWeight(ELimbEndEffector.LeftHand, 1.0f);
            humanik.SetIKRotationWeight(ELimbEndEffector.LeftHand, 1.0f);

            humanik.SetIKPositionWeight(ELimbEndEffector.RightHand, 1.0f);
            humanik.SetIKRotationWeight(ELimbEndEffector.RightHand, 1.0f);

            humanik.SetIKPositionWeight(ELimbEndEffector.LeftFoot, 1.0f);
            humanik.SetIKRotationWeight(ELimbEndEffector.LeftFoot, 1.0f);

            humanik.SetIKPositionWeight(ELimbEndEffector.RightFoot, 1.0f);
            humanik.SetIKRotationWeight(ELimbEndEffector.RightFoot, 1.0f);

            humanik.SetSpineWeight(0.0f);

            SceneNode ikTargetNode = rootNode.NewChild("IKTargetNode");
            var handTfm = humanComp.Left.Foot.Node!.GetTransformAs<Transform>(true)!;
            ikTargetNode.GetTransformAs<Transform>(true)!.SetFrameState(new TransformState()
            {
                Order = ETransformOrder.TRS,
                Rotation = handTfm.WorldRotation,
                Scale = new Vector3(1.0f),
                Translation = handTfm.WorldTranslation
            });
            humanik.GetGoalIK(ELimbEndEffector.LeftFoot)!.TargetIKTransform = ikTargetNode.Transform;
            EnableTransformToolForNode(ikTargetNode);
            Selection.SceneNode = ikTargetNode;
        }
        else
        {
            //var vrik = rootNode.AddComponent<VRIKSolverComponent>();
        }

        //TODO: only remove the head in VR
        if (VRPawn)
        {
            var headTfm = humanComp.Head?.Node?.GetTransformAs<Transform>();
            if (headTfm is not null)
                headTfm.Scale = Vector3.Zero;
        }

        if (VRPawn && Locomotion)
        {
            var footNode = rootNode.Parent!;
            var rotationNode = footNode.Parent!;
            var playspaceNode = rotationNode.Parent!;
            var player = playspaceNode.AddComponent<VRPlayerCharacterComponent>()!;
            player.HumanoidComponent = humanComp;
            player.EyeLBoneName = "Eye_L";
            player.EyeRBoneName = "Eye_R";

            VRPlayerInputSet input = playspaceNode.GetComponent<VRPlayerInputSet>()!;
            input.MuteToggled += (enabled) => player.EndCalibration();
        }

        if (TestAnimation)
        {
            var knee = humanComp!.Right.Knee?.Node?.Transform;
            var leg = humanComp!.Right.Leg?.Node?.Transform;

            leg?.RegisterAnimationTick<Scene.Transforms.Transform>(t => t.Rotation = Quaternion.CreateFromAxisAngle(Globals.Right, XRMath.DegToRad(180 - 90.0f * (MathF.Cos(Engine.ElapsedTime) * 0.5f + 0.5f))));
            knee?.RegisterAnimationTick<Scene.Transforms.Transform>(t => t.Rotation = Quaternion.CreateFromAxisAngle(Globals.Right, XRMath.DegToRad(90.0f * (MathF.Cos(Engine.ElapsedTime) * 0.5f + 0.5f))));

            //var rootTfm = rootNode.FirstChild.GetTransformAs<Transform>(true)!;
            ////rotate the root node in a circle, but still facing forward
            //rootTfm.RegisterAnimationTick<Transform>(t =>
            //{
            //    t.Translation = new Vector3(0, MathF.Sin(Engine.ElapsedTime), 0);
            //});

            //For testing blendshape morphing
            //Technically we should only register animation tick on scene node if any lods have blendshapes,
            //but at this point the model's meshes are still loading. So we'll just be lazy and check in the animation tick since it's just for testing.
            rootNode.IterateComponents<ModelComponent>(comp =>
            {
                comp.SceneNode.RegisterAnimationTick<SceneNode>(t =>
                {
                    var renderers = comp.Meshes.SelectMany(x => x.LODs).Select(x => x.Renderer).Where(x => x?.Mesh?.HasBlendshapes ?? false);
                    foreach (var renderer in renderers)
                    {
                        //for (int r = 0; r < xrMesh!.BlendshapeCount; r++)
                        int r = 0;
                        renderer?.SetBlendshapeWeightNormalized((uint)r, MathF.Sin(Engine.ElapsedTime) * 0.5f + 0.5f);
                    }
                });
            }, true);
        }

        if (PhysicsChain)
        {
            //Add physics chain to the breast bone
            var chest = humanComp!.Chest?.Node?.Transform;
            //Find breast bone
            if (chest is not null)
            {
                var earR = chest.FindDescendant(x =>
                    (x.Name?.Contains("KittenEarR", StringComparison.InvariantCultureIgnoreCase) ?? false));
                if (earR?.SceneNode is not null)
                {
                    var phys = earR.SceneNode.AddComponent<PhysicsChainComponent>()!;
                    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
                    phys.UpdateRate = 60;
                    phys.Damping = 0.1f;
                    phys.Inert = 0.0f;
                    phys.Stiffness = 0.05f;
                    //phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                    phys.Elasticity = 0.2f;
                    phys.Multithread = false;
                }

                var earL = chest.FindDescendant(x => 
                    (x.Name?.Contains("KittenEarL", StringComparison.InvariantCultureIgnoreCase) ?? false));
                if (earL?.SceneNode is not null)
                {
                    var phys = earL.SceneNode.AddComponent<PhysicsChainComponent>()!;
                    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
                    phys.UpdateRate = 60;
                    phys.Damping = 0.1f;
                    phys.Inert = 0.0f;
                    phys.Stiffness = 0.05f;
                    //phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                    phys.Elasticity = 0.2f;
                    phys.Multithread = false;
                }

                var breastL = chest.FindDescendant(x =>
                    (x.Name?.Contains("BreastUpper2_LRoot", StringComparison.InvariantCultureIgnoreCase) ?? false) || 
                    (x.Name?.Contains("Boob.L", StringComparison.InvariantCultureIgnoreCase) ?? false));
                if (breastL?.SceneNode is not null)
                {
                    var phys = breastL.SceneNode.AddComponent<PhysicsChainComponent>()!;
                    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
                    phys.UpdateRate = 60;
                    phys.Damping = 0.1f;
                    phys.Inert = 0.0f;
                    phys.Stiffness = 0.05f;
                    //phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                    phys.Elasticity = 0.2f;
                    phys.Multithread = false;
                }

                var breastR = chest.FindDescendant(x => 
                    (x.Name?.Contains("BreastUpper2_RRoot", StringComparison.InvariantCultureIgnoreCase) ?? false) || 
                    (x.Name?.Contains("Boob.R", StringComparison.InvariantCultureIgnoreCase) ?? false));
                if (breastR?.SceneNode is not null)
                {
                    var phys = breastR.SceneNode.AddComponent<PhysicsChainComponent>()!;
                    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
                    phys.UpdateRate = 60;
                    phys.Damping = 0.1f;
                    phys.Inert = 0.0f;
                    phys.Stiffness = 0.05f;
                    //phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                    phys.Elasticity = 0.2f;
                    phys.Multithread = false;
                }

                //var breastCenter = chest.FindChild(x => (x.Name?.Contains("Boob_Root", StringComparison.InvariantCultureIgnoreCase) ?? false));
                //if (breastCenter?.SceneNode is not null)
                //{
                //    var phys = breastCenter.SceneNode.AddComponent<PhysicsChainComponent>()!;
                //    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Normal;
                //    phys.UpdateRate = 60;
                //    phys.Damping = 0.1f;
                //    phys.Inert = 0.0f;
                //    phys.Stiffness = 0.05f;
                //    phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                //    phys.Elasticity = 0.2f;
                //    phys.Multithread = false;
                //}

                var tail = humanComp.Hips?.Node?.Transform.FindDescendant(x =>
                    x.Name?.Contains("Hair_1_2", StringComparison.InvariantCultureIgnoreCase) ?? false);
                if (tail?.SceneNode is not null)
                {
                    var phys = tail.SceneNode.AddComponent<PhysicsChainComponent>()!;
                    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
                    phys.UpdateRate = 60;
                    phys.Damping = 0.1f;
                    phys.Inert = 0.5f;
                    phys.Stiffness = 0.05f;
                    phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                    phys.Gravity = new Vector3(0.0f, -0.1f, 0.0f);
                    phys.Elasticity = 0.01f;
                    phys.Multithread = false;
                }

                //var longHair = humanComp.Head?.Node?.Transform.FindDescendant(x =>
                //    x.Name?.Contains("Long Hair", StringComparison.InvariantCultureIgnoreCase) ?? false);
                //if (longHair?.SceneNode is not null)
                //{
                //    //longHair.SceneNode.IsActiveSelf = false;
                //    var phys = longHair.SceneNode.AddComponent<PhysicsChainComponent>()!;
                //    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Normal;
                //    phys.UpdateRate = 60;
                //    phys.Damping = 0.1f;
                //    phys.Inert = 0.0f;
                //    phys.Stiffness = 0.05f;
                //    phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                //    phys.Elasticity = 0.2f;
                //}

                //var zafHair = humanComp.Head?.Node?.Transform.FindChild(x => x.Name?.Contains("Zaf Hair", StringComparison.InvariantCultureIgnoreCase) ?? false);
                //if (zafHair?.SceneNode is not null)
                //{
                //    var phys = zafHair.SceneNode.AddComponent<PhysicsChainComponent>()!;
                //    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.FixedUpdate;
                //    phys.UpdateRate = 60;
                //    phys.Damping = 0.01f;
                //    phys.Inert = 0.0f;
                //    phys.Stiffness = 0.01f;
                //    phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                //    phys.Elasticity = 0.1f;
                //}
            }
        }

        if (TransformTool)
        {
            //Show the transform tool for testing
            var root = humanComp!.SceneNode;
            if (root is null)
                return;

            EnableTransformToolForNode(root);
        }

        if (VMC)
        {
            var vmc = rootNode.AddComponent<VMCCaptureComponent>()!;
            vmc.Humanoid = humanComp;
        }

        if (FaceMotion3D)
        {
            var face = rootNode.AddComponent<FaceMotion3DCaptureComponent>()!;
            face.Humanoid = humanComp;
            //deactivate glasses node
            var glasses = humanComp.SceneNode?.FindDescendant(x => x.Name?.Contains("glasses", StringComparison.InvariantCultureIgnoreCase) ?? false);
            if (glasses != null)
                glasses.IsActiveSelf = false;
        }

        if (AnimationClipVMD)
        {
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var clip = Engine.Assets.Load<AnimationClip>(Path.Combine(desktopDir, "test.vmd"));
            if (clip is not null)
            {
                clip.Looped = true;

                var anim = rootNode.AddComponent<AnimationClipComponent>()!;
                anim.StartOnActivate = true;
                anim.Animation = clip;
            }
        }

        OVRLipSyncComponent? lipSync = null;
        if (AttachMicToAnimatedModel)
        {
            var headNode = humanComp!.Head?.Node?.Transform?.SceneNode;
            if (headNode is not null)
                AttachMicTo(headNode, out _, out _, out lipSync);
        }
        else if (LipSync)
            lipSync = Engine.State.MainPlayer.ControlledPawn?.SceneNode?.GetComponent<OVRLipSyncComponent>();
        
        if (lipSync is not null)
        {
            var face = rootNode.FindDescendantByName("Face", StringComparison.InvariantCultureIgnoreCase);
            if (face is not null)
            {
                if (face.TryGetComponent<ModelComponent>(out var comp))
                    lipSync.ModelComponent = comp;
                else
                {
                    face.ComponentAdded += SetModel;
                    void SetModel((SceneNode node, XRComponent comp) x)
                    {
                        if (x.comp is ModelComponent model)
                            lipSync.ModelComponent = model;
                        face.ComponentAdded -= SetModel;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Creates a test world with a variety of objects for testing purposes.
    /// </summary>
    /// <returns></returns>
    public static XRWorld CreateUnitTestWorld(bool setUI, bool isServer)
    {
        var s = Engine.Rendering.Settings;
        s.AllowBlendshapes = true;
        s.AllowSkinning = true;
        //s.RenderMesh3DBounds = true;
        s.RenderTransformDebugInfo = true;
        s.RenderTransformLines = false;
        s.RenderTransformCapsules = false;
        s.RenderTransformPoints = false;
        s.RecalcChildMatricesInParallel = true;
        s.TickGroupedItemsInParallel = true;
        s.RenderWindowsWhileInVR = true;
        s.AllowShaderPipelines = true; //Somehow, this lowers performance
        s.RenderVRSinglePassStereo = false;
        //s.PhysicsVisualizeSettings.SetAllTrue();

        string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        //UnityPackageExtractor.ExtractAsync(Path.Combine(desktopDir, "Animations.unitypackage"), Path.Combine(desktopDir, "Extracted"), true);

        //var anim = Engine.Assets.Load<Unity.UnityAnimationClip>(Path.Combine(desktopDir, "walk.anim"));
        //if (anim is not null)
        //{
        //    var anims = UnityConverter.ConvertFloatAnimation(anim);
        //}

        var scene = new XRScene("Main Scene");
        var rootNode = new SceneNode("Root Node");
        scene.RootNodes.Add(rootNode);

        if (VisualizeOctree)
            rootNode.AddComponent<DebugVisualizeOctreeComponent>();

        SceneNode? characterPawnModelParentNode = null;
        if (VRPawn)
        {
            if (Locomotion)
            {
                characterPawnModelParentNode = CreateCharacterVRPawn(rootNode, setUI, out var pawn, out _, out var leftHand, out var rightHand);
                if (setUI)
                    CreateEditorUI(characterPawnModelParentNode, null, pawn);

                if (AllowEditingInVR || AddCameraVRPickup)
                {
                    SceneNode cameraNode = CreateCamera(rootNode, out var camComp);
                    var pawn2 = CreateDesktopCamera(cameraNode, isServer, AllowEditingInVR && !AddCameraVRPickup, AddCameraVRPickup, false);
                    //TODO: swap editor ui between desktop and vr depending on how it was opened
                }
            }
            else
                CreateFlyingVRPawn(rootNode, setUI);
        }
        else if (Locomotion)
            characterPawnModelParentNode = CreateDesktopCharacterPawn(rootNode, setUI);
        else
        {
            SceneNode cameraNode = CreateCamera(rootNode, out var camComp);
            var pawn = CreateDesktopCamera(cameraNode, isServer, true, false, true);
            if (setUI)
                CreateEditorUI(rootNode, camComp!, pawn);
        }

        if (DirLight)
            AddDirLight(rootNode);
        if (SpotLight)
            AddSpotLight(rootNode);
        if (DirLight2)
            AddDirLight2(rootNode);
        if (PointLight)
            AddPointLight(rootNode);
        if (SoundNode)
            AddSoundNode(rootNode);
        if (IKTest)
            AddIKTest(rootNode);
        if (LightProbe || Skybox)
        {
            string[] names = ["warm_restaurant_4k", "overcast_soil_puresky_4k", "studio_small_09_4k", "klippad_sunrise_2_4k", "satara_night_4k"];
            Random r = new();
            XRTexture2D skyEquirect = Engine.Assets.LoadEngineAsset<XRTexture2D>("Textures", $"{names[r.Next(0, names.Length - 1)]}.exr");

            if (LightProbe)
                AddLightProbes(rootNode, 1, 1, 1, 10, 10, 10);
            if (Skybox)
                AddSkybox(rootNode, skyEquirect);
        }
        if (Mirror)
            AddMirror(rootNode);
        if (Physics)
            AddPhysics(rootNode, PhysicsBallCount);
        if (Spline)
            AddSpline(rootNode);
        if (DeferredDecal)
            AddDeferredDecal(rootNode);
        ImportModels(desktopDir, rootNode, characterPawnModelParentNode ?? rootNode);
        
        return new XRWorld("Default World", scene);
    }

    private static void AddMirror(SceneNode rootNode)
    {
        SceneNode mirrorNode = rootNode.NewChild("MirrorNode");
        var mirrorTfm = mirrorNode.SetTransform<Transform>();
        mirrorTfm.Translation = new Vector3(0.0f, 0.0f, -20.0f);
        mirrorTfm.Scale = new Vector3(160.0f, 90.0f, 1.0f);
        var mirrorComp = mirrorNode.AddComponent<MirrorCaptureComponent>()!;
    }

    private static void AddIKTest(SceneNode rootNode)
    {
        SceneNode ikTestRootNode = rootNode.NewChild();
        ikTestRootNode.Name = "IKTestRootNode";
        Transform tfmRoot = ikTestRootNode.GetTransformAs<Transform>(true)!;
        tfmRoot.Translation = new Vector3(0.0f, 0.0f, 0.0f);

        SceneNode ikTest1Node = ikTestRootNode.NewChild();
        ikTest1Node.Name = "IKTest1Node";
        Transform tfm1 = ikTest1Node.GetTransformAs<Transform>(true)!;
        tfm1.Translation = new Vector3(0.0f, 5.0f, 0.0f);

        SceneNode ikTest2Node = ikTest1Node.NewChild();
        ikTest2Node.Name = "IKTest2Node";
        Transform tfm2 = ikTest2Node.GetTransformAs<Transform>(true)!;
        tfm2.Translation = new Vector3(0.0f, 5.0f, 0.0f);

        var comp = ikTestRootNode.AddComponent<SingleTargetIKComponent>()!;
        comp.MaxIterations = 10;

        var targetNode = rootNode.NewChild();
        var targetTfm = targetNode.GetTransformAs<Transform>(true)!;
        targetTfm.Translation = new Vector3(2.0f, 5.0f, 0.0f);
        comp.TargetTransform = targetNode.Transform;

        //Let the user move the target
        EnableTransformToolForNode(targetNode);

        //string tree = ikTestRootNode.PrintTree();
        //Debug.Out(tree);
    }

    //Pawns are what the player controls in the game world.
    #region Pawns

    #region VR

    private static SceneNode CreateCharacterVRPawn(
        SceneNode rootNode,
        bool setUI,
        out CharacterPawnComponent pawn,
        out VRHeadsetTransform hmdTfm,
        out VRControllerTransform leftTfm,
        out VRControllerTransform rightTfm)
    {
        SceneNode vrPlayspaceNode = rootNode.NewChild("VRPlayspaceNode");
        var characterTfm = vrPlayspaceNode.SetTransform<RigidBodyTransform>();
        characterTfm.InterpolationMode = EInterpolationMode.Interpolate;

        CharacterPawnComponent characterComp = vrPlayspaceNode.AddComponent<CharacterPawnComponent>("TestPawn")!;
        pawn = characterComp;
        var vrInput = vrPlayspaceNode.AddComponent<VRPlayerInputSet>()!;
        vrInput.LeftHandOverlapChanged += OnLeftHandOverlapChanged;
        vrInput.RightHandOverlapChanged += OnRightHandOverlapChanged;
        vrInput.HandGrabbed += VrInput_HandGrabbed;
        var movementComp = vrPlayspaceNode.AddComponent<CharacterMovement3DComponent>()!;
        InitMovement(movementComp);

        //TODO: divert VR input from player 1 to this pawn instead of the flying editor pawn when AllowEditingInVR is true.
        if (!AllowEditingInVR)
            characterComp.EnqueuePossessionByLocalPlayer(ELocalPlayerIndex.One);

        SceneNode localRotationNode = vrPlayspaceNode.NewChild("LocalRotationNode");
        characterComp.IgnoreViewTransformPitch = true;
        characterComp.ViewRotationTransform = localRotationNode.GetTransformAs<Transform>(true)!;

        PawnComponent? refPawn = characterComp;
        characterComp.InputOrientationTransform = AddHeadsetNode(out hmdTfm, out _, localRotationNode, setUI, out var canvas, ref refPawn).Transform;

        AddHandControllerNode(out leftTfm, out _, localRotationNode, true);
        AddHandControllerNode(out rightTfm, out _, localRotationNode, false);

        if (canvas is not null)
        {
            void ShowDesktopMenu()
                => ShowMenu(canvas, true, vrPlayspaceNode.Transform);

            characterComp.PauseToggled += ShowDesktopMenu;

            void ShowVRMenu(bool leftHand)
                => ShowMenu(canvas, false, leftHand ? vrInput.LeftHandTransform : vrInput.RightHandTransform);

            vrInput.PauseToggled += ShowVRMenu;
        }

        AddTrackerCollectionNode(localRotationNode);
        vrInput.LeftHandTransform = leftTfm;
        vrInput.RightHandTransform = rightTfm;

        var footNode = localRotationNode.NewChild("Foot Position Node");
        var footTfm = footNode.SetTransform<Transform>();
        footTfm.Translation = new Vector3(0.0f, -movementComp.HalfHeight, 0.0f);
        //footTfm.Scale = new Vector3(movementComp.StandingHeight);
        footTfm.SaveBindState();

        //local rotation node only yaws to match the view yaw, so use it as the parent for the avatar
        return footNode;
    }

    private static void VrInput_HandGrabbed(VRPlayerInputSet sender, PhysxDynamicRigidBody item, bool left)
    {

    }

    private static void ChangeHighlight(PhysxDynamicRigidBody? prev, PhysxDynamicRigidBody? current)
    {
        DefaultRenderPipeline.SetHighlighted(prev, false);
        DefaultRenderPipeline.SetHighlighted(current, true);
    }

    private static void OnLeftHandOverlapChanged(VRPlayerInputSet set, PhysxDynamicRigidBody? prev, PhysxDynamicRigidBody? current)
        => ChangeHighlight(prev, current);
    private static void OnRightHandOverlapChanged(VRPlayerInputSet set, PhysxDynamicRigidBody? prev, PhysxDynamicRigidBody? current)
        => ChangeHighlight(prev, current);

    private static void InitMovement(CharacterMovement3DComponent movementComp)
    {
        movementComp.StandingHeight = 1.89f;
        movementComp.SpawnPosition = new Vector3(0.0f, 10.0f, 0.0f);
        movementComp.Velocity = new Vector3(0.0f, 0.0f, 0.0f);
        movementComp.JumpSpeed = 1.0f;
        //movementComp.GravityOverride = new Vector3(0.0f, -1.0f, 0.0f);
        movementComp.InputLerpSpeed = 0.9f;
    }

    private static void AddHandControllerNode(out VRControllerTransform controllerTfm, out VRControllerModelComponent modelComp, SceneNode parentNode, bool left)
    {
        SceneNode leftControllerNode = parentNode.NewChild($"VR{(left ? "Left" : "Right")}ControllerNode");

        controllerTfm = leftControllerNode.SetTransform<VRControllerTransform>();
        controllerTfm.LeftHand = left;
        controllerTfm.ForceManualRecalc = false;

        modelComp = leftControllerNode.AddComponent<VRControllerModelComponent>()!;
        modelComp.LeftHand = left;
    }
    private static void CreateFlyingVRPawn(SceneNode rootNode, bool setUI)
    {
        SceneNode vrPlayspaceNode = new(rootNode) { Name = "VRPlayspaceNode" };
        var playspaceTfm = vrPlayspaceNode.SetTransform<Transform>();
        PawnComponent? pawn = null;
        AddHeadsetNode(out _, out _, vrPlayspaceNode, setUI, out _, ref pawn);
        AddHandControllerNode(out _, out _, vrPlayspaceNode, true);
        AddHandControllerNode(out _, out _, vrPlayspaceNode, false);
        AddTrackerCollectionNode(vrPlayspaceNode);
        VRPlayerInputSet? input = pawn?.SceneNode?.AddComponent<VRPlayerInputSet>()!;
    }

    private static void AddTrackerCollectionNode(SceneNode vrPlayspaceNode)
        => vrPlayspaceNode.NewChild<VRTrackerCollectionComponent>(out _, "VRTrackerCollectionNode");

    private static SceneNode AddHeadsetNode(out VRHeadsetTransform hmdTfm, out VRHeadsetComponent hmdComp, SceneNode parentNode, bool setUI, out UICanvasComponent? canvas, ref PawnComponent? pawn)
    {
        canvas = null;

        SceneNode vrHeadsetNode = parentNode.NewChild("VRHeadsetNode");
        var listener = vrHeadsetNode.AddComponent<AudioListenerComponent>("VR HMD Listener")!;
        listener.Gain = 1.0f;
        listener.DistanceModel = DistanceModel.InverseDistance;
        listener.DopplerFactor = 0.5f;
        listener.SpeedOfSound = 343.3f;

        hmdTfm = vrHeadsetNode.SetTransform<VRHeadsetTransform>()!;
        hmdComp = vrHeadsetNode.AddComponent<VRHeadsetComponent>()!;

        if (!AllowEditingInVR)
        {
            SceneNode firstPersonViewNode = new(vrHeadsetNode) { Name = "FirstPersonViewNode" };
            var firstPersonViewTfm = firstPersonViewNode.SetTransform<SmoothedParentConstraintTransform>();
            firstPersonViewTfm.TranslationInterpolationSpeed = null;
            firstPersonViewTfm.ScaleInterpolationSpeed = null;
            firstPersonViewTfm.QuaternionInterpolationSpeed = null;
            //firstPersonViewTfm.SplitYPR = true;
            //firstPersonViewTfm.YawInterpolationSpeed = 5.0f;
            //firstPersonViewTfm.PitchInterpolationSpeed = 5.0f;
            //firstPersonViewTfm.IgnoreRoll = true;
            //firstPersonViewTfm.UseLookAtYawPitch = true;
            var firstPersonCam = firstPersonViewNode.AddComponent<CameraComponent>()!;
            var persp = firstPersonCam.Camera.Parameters as XRPerspectiveCameraParameters;
            persp!.HorizontalFieldOfView = 50.0f;
            persp.NearZ = 0.1f;
            persp.FarZ = 100000.0f;
            firstPersonCam.CullWithFrustum = true;
            if (pawn is null)
                pawn = firstPersonCam.SetAsPlayerView(ELocalPlayerIndex.One);
            else
                pawn.CameraComponent = firstPersonCam;

            //if (setUI)
            //    canvas = CreateEditorUI(vrHeadsetNode, firstPersonCam);
        }

        return vrHeadsetNode;
    }
    #endregion

    #region Desktop

    private static PawnComponent? CreateDesktopCamera(SceneNode cameraNode, bool isServer, bool flyable, bool addPhysicsBody, bool addListener)
    {
        if (addPhysicsBody)
        {
            IPhysicsGeometry.Sphere s = new(0.2f);
            PhysxMaterial mat = new(0.5f, 0.5f, 0.5f);
            PhysxShape shape = new(s, mat, PxShapeFlags.TriggerShape | PxShapeFlags.Visualization, true);
            var cameraPickup = cameraNode.AddComponent<DynamicRigidBodyComponent>()!;
            PhysxDynamicRigidBody body = new(shape, 1.0f);
            cameraPickup.RigidBody = body;

            body.Mass = 1.0f;
            body.Flags = 0;
            body.GravityEnabled = false;
            body.SimulationEnabled = true;
            body.DebugVisualize = true;
        }

        if (addListener)
        {
            var listener = cameraNode.AddComponent<AudioListenerComponent>("Desktop Flying Listener")!;
            listener.Gain = 1.0f;
            listener.DistanceModel = DistanceModel.InverseDistance;
            listener.DopplerFactor = 0.5f;
            listener.SpeedOfSound = 343.3f;
        }

        if (!(VRPawn && AllowEditingInVR) && Microphone && !(AnimatedModel && AttachMicToAnimatedModel))
            AttachMicTo(cameraNode, out _, out _, out _);
        
        PawnComponent pawnComp;
        if (flyable)
        {
            pawnComp = cameraNode.AddComponent<EditorFlyingCameraPawnComponent>()!;
            pawnComp!.Name = "Desktop Camera Pawn (Flyable)";
        }
        else
        {
            pawnComp = cameraNode.AddComponent<PawnComponent>()!;
            pawnComp!.Name = "Desktop Camera Pawn";
        }

        pawnComp.EnqueuePossessionByLocalPlayer(ELocalPlayerIndex.One);
        return pawnComp;
    }

    private static void AttachMicTo(SceneNode node, out AudioSourceComponent? source, out MicrophoneComponent? mic, out OVRLipSyncComponent? lipSync)
    {
        source = null;
        mic = null;
        lipSync = null;

        if (!Microphone)
            return;
        
        source = node.AddComponent<AudioSourceComponent>()!;
        source.Loop = false;
        source.Pitch = 1.0f;
        source.IsDirectional = false;
        source.ConeInnerAngle = 0.0f;
        source.ConeOuterAngle = 90.0f;
        source.ConeOuterGain = 1.0f;

        mic = node.AddComponent<MicrophoneComponent>()!;
        mic.Capture = true;//!isServer;
        mic.Receive = true;//isServer;
        mic.Loopback = false; //For testing, set to true to hear your own voice, unless both capture and receive are true on the client.

        if (LipSync)
        {
            lipSync = node.AddComponent<OVRLipSyncComponent>()!;
            lipSync.VisemeNamePrefix = "vrc.v_";
        }
    }

    private static SceneNode CreateDesktopCharacterPawn(SceneNode rootNode, bool setUI)
    {
        SceneNode characterNode = new(rootNode, "Player");
        var characterTfm = characterNode.SetTransform<RigidBodyTransform>();
        characterTfm.InterpolationMode = EInterpolationMode.Interpolate;

        //create node to translate camera up half the height of the character
        SceneNode cameraOffsetNode = new(characterNode, "Camera Offset");
        var cameraOffsetTfm = cameraOffsetNode.SetTransform<Transform>();
        cameraOffsetTfm.Translation = new Vector3(0.0f, 1.0f, 0.0f);

        SceneNode cameraParentNode;
        if (ThirdPersonPawn)
        {
            //Create camera boom with sphere shapecast
            cameraParentNode = cameraOffsetNode.NewChildWithTransform<BoomTransform>(out var boomTfm, "3rd Person Camera Boom");
            boomTfm.MaxLength = 10.0f;
            boomTfm.ZoomOutSpeed = 5.0f;
        }
        else
            cameraParentNode = cameraOffsetNode;

        SceneNode cameraNode = CreateCamera(cameraParentNode, out CameraComponent? camComp, null, !ThirdPersonPawn);

        var listener = cameraNode.AddComponent<AudioListenerComponent>("Desktop Character Listener")!;
        listener.Gain = 1.0f;
        listener.DistanceModel = DistanceModel.InverseDistance;
        listener.DopplerFactor = 0.5f;
        listener.SpeedOfSound = 343.3f;

        var characterComp = characterNode.AddComponent<CharacterPawnComponent>("TestPawn")!;
        characterComp.CameraComponent = camComp;
        characterComp.InputOrientationTransform = cameraNode.Transform;
        characterComp.ViewRotationTransform = cameraOffsetTfm;

        var movementComp = characterNode.AddComponent<CharacterMovement3DComponent>()!;
        InitMovement(movementComp);

        characterComp.EnqueuePossessionByLocalPlayer(ELocalPlayerIndex.One);

        if (camComp is not null && setUI)
        {
            UICanvasComponent canvas = CreateEditorUI(characterNode, camComp);
            canvas.IsActive = false;
            characterComp.PauseToggled += () => ShowMenu(canvas, true, characterNode.Transform);
        }

        var footNode = characterNode.NewChild("Foot Position Node");
        var footTfm = footNode.SetTransform<Transform>();
        footTfm.Translation = new Vector3(0.0f, -movementComp.HalfHeight, 0.0f);
        //footTfm.Scale = new Vector3(movementComp.StandingHeight);
        footTfm.SaveBindState();

        return footNode;
    }

    #endregion

    private static SceneNode CreateCamera(SceneNode parentNode, out CameraComponent? camComp, float? smoothed = 30.0f, bool localSmoothing = true)
    {
        var cameraNode = new SceneNode(parentNode, "TestCameraNode");

        if (smoothed.HasValue)
        {
            if (localSmoothing)
            {
                var laggedTransform = cameraNode.GetTransformAs<SmoothedTransform>(true)!;
                float smooth = smoothed.Value;
                laggedTransform.RotationSmoothingSpeed = smooth;
                laggedTransform.TranslationSmoothingSpeed = smooth;
                laggedTransform.ScaleSmoothingSpeed = smooth;
            }
            else
            {
                var laggedTransform = cameraNode.GetTransformAs<SmoothedParentConstraintTransform>(true)!;
                float smooth = smoothed.Value;
                laggedTransform.TranslationInterpolationSpeed = smooth;
                laggedTransform.ScaleInterpolationSpeed = null;
                laggedTransform.QuaternionInterpolationSpeed = null;
            }
        }

        if (cameraNode.TryAddComponent(out camComp, "TestCamera"))
            camComp!.SetPerspective(60.0f, 0.1f, 100000.0f, null);
        else
            camComp = null;

        return cameraNode;
    }

    #endregion

    //All tests pertaining to shading the scene.
    #region Shading Tests

    //Code for lighting the scene.
    #region Lights

    private static void AddLightProbes(SceneNode rootNode, int heightCount, int widthCount, int depthCount, float height, float width, float depth)
    {
        var probeRoot = new SceneNode(rootNode) { Name = "LightProbeRoot" };

        float halfWidth = width * 0.5f;
        float halfDepth = depth * 0.5f;

        for (int i = 0; i < heightCount; i++)
        {
            float h = i * height;
            for (int j = 0; j < widthCount; j++)
            {
                float w = j * width;
                for (int k = 0; k < depthCount; k++)
                {
                    float d = k * depth;

                    var probe = new SceneNode(probeRoot) { Name = $"LightProbe_{i}_{j}_{k}" };
                    var probeTransform = probe.SetTransform<Transform>();
                    probeTransform.Translation = new Vector3(w - halfWidth, h, d - halfDepth);
                    var probeComp = probe.AddComponent<LightProbeComponent>();

                    probeComp!.Name = "TestLightProbe";
                    probeComp.SetCaptureResolution(64, false);
                    probeComp.RealtimeCapture = true;
                    probeComp.PreviewDisplay = LightProbeComponent.ERenderPreview.Irradiance;
                    probeComp.RealTimeCaptureUpdateInterval = TimeSpan.FromMilliseconds(LightProbeCaptureMs);
                    if (StopRealtimeCaptureSec is not null)
                        probeComp.StopRealtimeCaptureAfter = TimeSpan.FromSeconds(StopRealtimeCaptureSec.Value);
                }
            }
        }
    }

    private static void AddDirLight(SceneNode rootNode)
    {
        var dirLightNode = new SceneNode(rootNode) { Name = "TestDirectionalLightNode" };
        var dirLightTransform = dirLightNode.SetTransform<Transform>();
        dirLightTransform.Translation = new Vector3(0.0f, 0.0f, 0.0f);
        //Face the light directly down
        dirLightTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, XRMath.DegToRad(-70.0f));
        //dirLightTransform.RegisterAnimationTick<Transform>(t => t.Rotation *= Quaternion.CreateFromAxisAngle(Globals.Backward, Engine.DilatedDelta));
        if (!dirLightNode.TryAddComponent<DirectionalLightComponent>(out var dirLightComp))
            return;

        dirLightComp!.Name = "TestDirectionalLight";
        dirLightComp.Color = new Vector3(1, 1, 1);
        dirLightComp.DiffuseIntensity = 1.0f;
        dirLightComp.Scale = new Vector3(1000.0f, 1000.0f, 1000.0f);
        dirLightComp.CastsShadows = true;
        dirLightComp.SetShadowMapResolution(4096, 4096);
    }

    private static void AddDirLight2(SceneNode rootNode)
    {
        var dirLightNode2 = new SceneNode(rootNode) { Name = "TestDirectionalLightNode2" };
        var dirLightTransform2 = dirLightNode2.SetTransform<Transform>();
        dirLightTransform2.Translation = new Vector3(0.0f, 10.0f, 0.0f);
        dirLightTransform2.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2.0f);
        if (!dirLightNode2.TryAddComponent<DirectionalLightComponent>(out var dirLightComp2))
            return;

        dirLightComp2!.Name = "TestDirectionalLight2";
        dirLightComp2.Color = new Vector3(1.0f, 0.8f, 0.8f);
        dirLightComp2.DiffuseIntensity = 1.0f;
        dirLightComp2.Scale = new Vector3(1000.0f, 1000.0f, 1000.0f);
        dirLightComp2.CastsShadows = false;
    }

    private static void AddSpotLight(SceneNode rootNode)
    {
        var spotLightNode = new SceneNode(rootNode) { Name = "TestSpotLightNode" };
        var spotLightTransform = spotLightNode.SetTransform<Transform>();
        spotLightTransform.Translation = new Vector3(0.0f, 10.0f, 0.0f);
        spotLightTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, XRMath.DegToRad(-90.0f));
        if (!spotLightNode.TryAddComponent<SpotLightComponent>(out var spotLightComp))
            return;

        spotLightComp!.Name = "TestSpotLight";
        spotLightComp.Color = new Vector3(1.0f, 1.0f, 1.0f);
        spotLightComp.DiffuseIntensity = 10.0f;
        spotLightComp.Brightness = 5.0f;
        spotLightComp.Distance = 40.0f;
        spotLightComp.SetCutoffs(10, 40);
        spotLightComp.CastsShadows = true;
        spotLightComp.SetShadowMapResolution(2048, 2048);
    }

    private static void AddPointLight(SceneNode rootNode)
    {
        var pointLight = new SceneNode(rootNode) { Name = "TestPointLightNode" };
        var pointLightTransform = pointLight.SetTransform<Transform>();
        pointLightTransform.Translation = new Vector3(0.0f, 2.0f, 0.0f);
        if (!pointLight.TryAddComponent<PointLightComponent>(out var pointLightComp))
            return;

        pointLightComp!.Name = "TestPointLight";
        pointLightComp.Color = new Vector3(1.0f, 1.0f, 1.0f);
        pointLightComp.DiffuseIntensity = 10.0f;
        pointLightComp.Brightness = 10.0f;
        pointLightComp.Radius = 10000.0f;
        pointLightComp.CastsShadows = true;
        pointLightComp.SetShadowMapResolution(1024, 1024);
    }

    #endregion

    //Deferred decals are textures that are projected onto the scene geometry before the lighting pass using the GBuffer.
    private static void AddDeferredDecal(SceneNode rootNode)
    {
        var decalNode = new SceneNode(rootNode) { Name = "TestDecalNode" };
        var decalTfm = decalNode.SetTransform<Transform>();
        decalTfm.Translation = new Vector3(0.0f, 5.0f, 0.0f);
        decalTfm.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, XRMath.DegToRad(70.0f));
        decalTfm.Scale = new Vector3(7.0f);
        var decalComp = decalNode.AddComponent<DeferredDecalComponent>()!;
        decalComp.Name = "TestDecal";
        decalComp.SetTexture(Engine.Assets.LoadEngineAsset<XRTexture2D>("Textures", "decal guide.png"));
    }

    //private static void AddPBRTestOrbs(SceneNode rootNode, float y)
    //{
    //    for (int metallic = 0; metallic < 10; metallic++)
    //        for (int roughness = 0; roughness < 10; roughness++)
    //            AddPBRTestOrb(rootNode, metallic / 10.0f, roughness / 10.0f, 0.5f, 0.5f, 10, 10, y);
    //}

    //private static void AddPBRTestOrb(SceneNode rootNode, float metallic, float roughness, float radius, float padding, int metallicCount, int roughnessCount, float y)
    //{
    //    var orb1 = new SceneNode(rootNode) { Name = "TestOrb1" };
    //    var orb1Transform = orb1.SetTransform<Transform>();

    //    //arrange in grid using metallic and roughness
    //    orb1Transform.Translation = new Vector3(
    //        (metallic * 2.0f - 1.0f) * (radius + padding) * metallicCount,
    //        y + padding + radius,
    //        (roughness * 2.0f - 1.0f) * (radius + padding) * roughnessCount);

    //    var orb1Model = orb1.AddComponent<ModelComponent>()!;
    //    var mat = XRMaterial.CreateLitColorMaterial(ColorF4.Red);
    //    mat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferredLit;
    //    mat.Parameter<ShaderFloat>("Roughness")!.Value = roughness;
    //    mat.Parameter<ShaderFloat>("Metallic")!.Value = metallic;
    //    orb1Model.Model = new Model([new SubMesh(XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, 32), mat)]);
    //}

    #endregion

    //User interface overlay code.
    #region UI

    private const bool DockFPSTopLeft = false;

    //Simple FPS counter in the bottom right for debugging.
    private static UITextComponent AddFPSText(FontGlyphSet? font, SceneNode parentNode)
    {
        SceneNode textNode = new(parentNode) { Name = "TestTextNode" };
        UITextComponent text = textNode.AddComponent<UITextComponent>()!;
        text.Font = font;
        text.FontSize = 22;
        text.WrapMode = FontGlyphSet.EWrapMode.None;
        text.RegisterAnimationTick<UITextComponent>(TickFPS);
        var textTransform = textNode.GetTransformAs<UIBoundableTransform>(true)!;
        if (DockFPSTopLeft)
        {
            textTransform.MinAnchor = new Vector2(0.0f, 1.0f);
            textTransform.MaxAnchor = new Vector2(0.0f, 1.0f);
            textTransform.NormalizedPivot = new Vector2(0.0f, 1.0f);
        }
        else
        {
            textTransform.MinAnchor = new Vector2(1.0f, 0.0f);
            textTransform.MaxAnchor = new Vector2(1.0f, 0.0f);
            textTransform.NormalizedPivot = new Vector2(1.0f, 0.0f);
        }
        textTransform.Width = null;
        textTransform.Height = null;
        textTransform.Margins = new Vector4(10.0f, 10.0f, 10.0f, 10.0f);
        textTransform.Scale = new Vector3(1.0f);
        return text;
    }

    private static void ShowMenu(UICanvasComponent canvas, bool screenSpace, TransformBase? parent)
    {
        var canvasTfm = canvas.CanvasTransform;
        canvasTfm.Parent = parent;
        canvasTfm.DrawSpace = screenSpace ? ECanvasDrawSpace.Screen : ECanvasDrawSpace.World;
        canvas.IsActive = !canvas.IsActive;
    }

    //The full editor UI - includes a toolbar, inspector, viewport and scene hierarchy.
    private static UICanvasComponent CreateEditorUI(SceneNode parent, CameraComponent? screenSpaceCamera, PawnComponent? pawnForInput = null)
    {
        var rootCanvasNode = new SceneNode(parent) { Name = "TestUINode" };
        var canvas = rootCanvasNode.AddComponent<UICanvasComponent>()!;
        var canvasTfm = rootCanvasNode.GetTransformAs<UICanvasTransform>(true)!;
        canvasTfm.DrawSpace = ECanvasDrawSpace.Screen;
        canvasTfm.Width = 1920.0f;
        canvasTfm.Height = 1080.0f;
        canvasTfm.CameraDrawSpaceDistance = 10.0f;
        canvasTfm.Padding = new Vector4(0.0f);

        if (VisualizeQuadtree)
            rootCanvasNode.AddComponent<DebugVisualizeQuadtreeComponent>();

        if (screenSpaceCamera is not null)
            screenSpaceCamera.UserInterface = canvas;

        AddFPSText(null, rootCanvasNode);

        if (AddEditorUI)
        {
            //Add input handler
            var input = rootCanvasNode.AddComponent<UIInputComponent>()!;
            input.OwningPawn = pawnForInput;

            //This will take care of editor UI arrangement operations for us
            var mainUINode = rootCanvasNode.NewChild<UIEditorComponent>(out UIEditorComponent? editorComp);
            if (editorComp.UITransform is UIBoundableTransform tfm)
            {
                tfm.MinAnchor = new Vector2(0.0f, 0.0f);
                tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
                tfm.NormalizedPivot = new Vector2(0.0f, 0.0f);
                tfm.Translation = new Vector2(0.0f, 0.0f);
                tfm.Width = null;
                tfm.Height = null;
            }
            _editorComponent = editorComp;
            RemakeMenu();

            GameCSProjLoader.OnAssemblyLoaded += GameCSProjLoader_OnAssemblyLoaded;
            GameCSProjLoader.OnAssemblyUnloaded += GameCSProjLoader_OnAssemblyUnloaded;
        }

        return canvas;
    }

    private static UIEditorComponent? _editorComponent = null;

    private static void GameCSProjLoader_OnAssemblyUnloaded(string obj) => RemakeMenu();
    private static void GameCSProjLoader_OnAssemblyLoaded(string arg1, GameCSProjLoader.AssemblyData arg2) => RemakeMenu();

    private static void RemakeMenu()
    {
        if (_editorComponent is not null)
            _editorComponent.RootMenuOptions = GenerateRootMenu();
    }

    //Signals the camera to take a picture of the current view.
    public static void TakeScreenshot(UIInteractableComponent comp)
    {
        //Debug.Out("Take Screenshot clicked");

        var camera = Engine.State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One).ControlledPawn as EditorFlyingCameraPawnComponent;
        camera?.TakeScreenshot();
    }
    //Loads a project from the file system.
    public static void LoadProject(UIInteractableComponent comp)
    {
        //Debug.Out("Load Project clicked");
    }
    //Saves all modified assets in the project.
    public static async void SaveAll(UIInteractableComponent comp)
    {
        await Engine.Assets.SaveAllAsync();
    }

    //Generates the root menu for the editor UI.
    //TODO: allow scripts to add menu options with attributes
    private static List<ToolbarItemBase> GenerateRootMenu()
    {
        List<ToolbarItemBase> buttons = [
            new ToolbarButton("File", [Key.ControlLeft, Key.F],
            [
                new ToolbarButton("Save All", SaveAll),
                new ToolbarButton("Open", [
                    new ToolbarButton("Project", LoadProject),
                    ])
            ]),
            new ToolbarButton("Edit"),
            new ToolbarButton("Assets"),
            new ToolbarButton("Tools", [Key.ControlLeft, Key.T],
            [
                new ToolbarButton("Take Screenshot", TakeScreenshot),
            ]),
            new ToolbarButton("View"),
            new ToolbarButton("Window"),
            new ToolbarButton("Help"),
        ];

        //Add dynamically loaded menu options
        foreach (GameCSProjLoader.AssemblyData assembly in GameCSProjLoader.LoadedAssemblies.Values)
        {
            foreach (Type menuItem in assembly.MenuItems)
            {
                if (!menuItem.IsSubclassOf(typeof(ToolbarItemBase)))
                    continue;
                
                buttons.Add((ToolbarItemBase)Activator.CreateInstance(menuItem)!);
            }
        }
        
        return buttons;
    }

    #endregion

    //All tests pertaining to lighting the scene.
    #region Physics Tests

    //Creates a floor and a bunch of balls that fall onto it.
    private static void AddPhysics(SceneNode rootNode, int ballCount)
    {
        AddPhysicsFloor(rootNode);
        AddPhysicsSpheres(rootNode, ballCount);
    }

    private static void AddPhysicsSpheres(SceneNode rootNode, int count, float radius = 0.5f)
    {
        if (count <= 0)
            return;
        
        Random random = new();
        PhysxMaterial physMat = new(0.2f, 0.2f, 1.0f);
        for (int i = 0; i < count; i++)
            AddBall(rootNode, physMat, radius, random);
    }

    private static void AddPhysicsFloor(SceneNode rootNode)
    {
        var floor = new SceneNode(rootNode) { Name = "Floor" };
        var floorTfm = floor.SetTransform<RigidBodyTransform>();
        var floorComp = floor.AddComponent<StaticRigidBodyComponent>()!;

        PhysxMaterial floorPhysMat = new(0.5f, 0.5f, 0.7f);

        var floorBody = PhysxStaticRigidBody.CreatePlane(Globals.Up, 0.0f, floorPhysMat);
        //new PhysxStaticRigidBody(floorMat, new PhysxGeometry.Box(new Vector3(100.0f, 2.0f, 100.0f)));
        floorBody.SetTransform(new Vector3(0.0f, 0.0f, 0.0f), Quaternion.CreateFromAxisAngle(Globals.Backward, XRMath.DegToRad(90.0f)), true);
        //floorBody.CollisionGroup = 1;
        //floorBody.GroupsMask = new MagicPhysX.PxGroupsMask() { bits0 = 0, bits1 = 0, bits2 = 0, bits3 = 1 };
        floorComp.RigidBody = floorBody;
        //floorBody.AddedToScene += x =>
        //{
        //    var shapes = floorBody.GetShapes();
        //    var shape = shapes[0];
        //    //shape.QueryFilterData = new MagicPhysX.PxFilterData() { word0 = 0, word1 = 0, word2 = 0, word3 = 1 };
        //};

        //var floorShader = ShaderHelper.LoadEngineShader("Misc\\TestFloor.frag");
        //ShaderVar[] floorUniforms =
        //[
        //    new ShaderVector4(new ColorF4(0.9f, 0.9f, 0.9f, 1.0f), "MatColor"),
        //    new ShaderFloat(10.0f, "BlurStrength"),
        //    new ShaderInt(20, "SampleCount"),
        //    new ShaderVector3(Globals.Up, "PlaneNormal"),
        //];
        //XRTexture2D grabTex = XRTexture2D.CreateGrabPassTextureResized(0.2f);
        //XRMaterial floorMat = new(floorUniforms, [grabTex], floorShader);
        XRMaterial floorMat = XRMaterial.CreateLitColorMaterial(ColorF4.Gray);
        floorMat.RenderOptions.CullMode = ECullMode.None;
        //floorMat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
        floorMat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferredLit;
        //floorMat.EnableTransparency();

        var floorModel = floor.AddComponent<ModelComponent>()!;
        floorModel.Model = new Model([new SubMesh(XRMesh.Create(VertexQuad.PosY(10000.0f)), floorMat)]);
    }

    //Spawns a ball with a random position, velocity and angular velocity.
    private static void AddBall(SceneNode rootNode, PhysxMaterial ballPhysMat, float ballRadius, Random random)
    {
        var ballBody = new PhysxDynamicRigidBody(ballPhysMat, new IPhysicsGeometry.Sphere(ballRadius), 1.0f)
        {
            Transform = (new Vector3(
                random.NextSingle() * 100.0f,
                random.NextSingle() * 100.0f,
                random.NextSingle() * 100.0f), Quaternion.Identity),
            AngularDamping = 0.2f,
            LinearDamping = 0.2f,
        };

        ballBody.SetAngularVelocity(new Vector3(
            random.NextSingle() * 100.0f,
            random.NextSingle() * 100.0f,
            random.NextSingle() * 100.0f));

        ballBody.SetLinearVelocity(new Vector3(
            random.NextSingle() * 10.0f,
            random.NextSingle() * 10.0f,
            random.NextSingle() * 10.0f));

        var ball = new SceneNode(rootNode) { Name = "Ball" };
        var ballTfm = ball.SetTransform<RigidBodyTransform>();
        ballTfm.InterpolationMode = EInterpolationMode.Interpolate;
        var ballComp = ball.AddComponent<DynamicRigidBodyComponent>()!;
        ballComp.RigidBody = ballBody;
        var ballModel = ball.AddComponent<ModelComponent>()!;

        ColorF4 color = new(
            random.NextSingle(),
            random.NextSingle(),
            random.NextSingle());

        var ballMat = XRMaterial.CreateLitColorMaterial(color);
        ballMat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferredLit;
        ballMat.Parameter<ShaderFloat>("Roughness")!.Value = random.NextSingle();
        ballMat.Parameter<ShaderFloat>("Metallic")!.Value = random.NextSingle();
        ballModel.Model = new Model([new SubMesh(XRMesh.Shapes.SolidSphere(Vector3.Zero, ballRadius, 32), ballMat)]);
    }

    #endregion

    //Tests for transforming scene nodes via animations and methods.
    #region Animation Tests

    private static void AddSpline(SceneNode rootNode)
    {
        var spline = rootNode.AddComponent<Spline3DPreviewComponent>();
        PropAnimVector3 anim = new();
        Random r = new();
        float len = r.NextSingle() * 10.0f;
        int frameCount = r.Next(2, 10);
        for (int i = 0; i < frameCount; i++)
        {
            float t = i / (float)frameCount;
            Vector3 value = new(r.NextSingle() * 10.0f, r.NextSingle() * 10.0f, r.NextSingle() * 10.0f);
            Vector3 tangent = new(r.NextSingle() * 10.0f, r.NextSingle() * 10.0f, r.NextSingle() * 10.0f);
            anim.Keyframes.Add(new Vector3Keyframe(t, value, tangent, EVectorInterpType.Smooth));
        }
        anim.LengthInSeconds = len;
        spline!.Spline = anim;
    }

    #endregion

    //Tests for audio sources and listeners.
    #region Sound Tests

    private static void AddSoundNode(SceneNode rootNode)
    {
        var sound = new SceneNode(rootNode) { Name = "TestSoundNode" };
        if (!sound.TryAddComponent<AudioSourceComponent>(out var soundComp))
            return;

        soundComp!.Name = "TestSound";
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var data = Engine.Assets.Load<AudioData>(Path.Combine(desktop, "test.mp3"));
        //var data = Engine.Assets.LoadEngineAsset<AudioData>("Audio", "test16bit.wav");
        //data!.ConvertToMono(); //Convert to mono for 3D audio - stereo will just play equally in both ears
        soundComp.RelativeToListener = true;
        //soundComp.ReferenceDistance = 1.0f;
        ////soundComp.MaxDistance = 100.0f;
        //soundComp.RolloffFactor = 1.0f;
        soundComp.Gain = 0.1f;
        soundComp.Loop = true;
        soundComp.StaticBuffer = data;
        soundComp.PlayOnActivate = true;
    }

    #endregion

    private static void ImportModels(string desktopDir, SceneNode rootNode, SceneNode characterParentNode)
    {
        var importedModelsNode = new SceneNode(rootNode) { Name = "TestImportedModelsNode" };
        string fbxPathDesktop = Path.Combine(desktopDir, "misc", "jax2.fbx");

        var animFlags = 
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

        var staticFlags =
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

        if (AnimatedModel)
        {
            SceneNode? ImportAnimated()
            {
                using var importer = new ModelImporter(fbxPathDesktop, null, null);
                importer.MakeMaterialAction = MakeMaterial;
                importer.MakeTextureAction = MakeTexture;
                var node = importer.Import(animFlags, true, true, ModelScale, ModelZUp, true);
                if (characterParentNode != null && node != null)
                    characterParentNode.Transform.AddChild(node.Transform, false, true);
                return node;
            }
            Task.Run(ImportAnimated).ContinueWith(nodeTask => OnFinishedImportingAvatar(nodeTask.Result));
            //ModelImporter.ImportAsync(fbxPathDesktop, animFlags, null, null, characterParentNode, ModelScale, ModelZUp).ContinueWith(OnFinishedAvatarAsync);
        }
        if (StaticModel)
        {
            string path = Path.Combine(Engine.Assets.EngineAssetsPath, "Models", "Sponza", "sponza.obj");

            //string path2 = Path.Combine(Engine.Assets.EngineAssetsPath, "Models", "main1_sponza", "NewSponza_Main_Yup_003.fbx");
            //var task2 = ModelImporter.ImportAsync(path2, staticFlags, null, null, importedModelsNode, 1, false).ContinueWith(OnFinishedWorld);

            //string path = Path.Combine(Engine.Assets.EngineAssetsPath, "Models", "pkg_a_curtains", "NewSponza_Curtains_FBX_YUp.fbx");
            var task1 = ModelImporter.ImportAsync(path, staticFlags, null, null, importedModelsNode, 1, false).ContinueWith(OnFinishedWorld);

            //await Task.WhenAll(task1, task2);
        }
    }

    private static void AddSkybox(SceneNode rootNode, XRTexture2D skyEquirect)
    {
        var skybox = new SceneNode(rootNode) { Name = "TestSkyboxNode" };
        //var skyboxTransform = skybox.SetTransform<Transform>();
        //skyboxTransform.Translation = new Vector3(0.0f, 0.0f, 0.0f);
        if (!skybox.TryAddComponent<ModelComponent>(out var skyboxComp))
            return;

        skyboxComp!.Name = "TestSkybox";
        skyboxComp.Model = new Model([new SubMesh(
            XRMesh.Shapes.SolidBox(new Vector3(-9000), new Vector3(9000), true, XRMesh.Shapes.ECubemapTextureUVs.None),
            new XRMaterial([skyEquirect], Engine.Assets.LoadEngineAsset<XRShader>("Shaders", "Scene3D", "Equirect.fs"))
            {
                RenderPass = (int)EDefaultRenderPass.Background,
                RenderOptions = new RenderingParameters()
                {
                    CullMode = ECullMode.Back,
                    DepthTest = new DepthTest()
                    {
                        UpdateDepth = false,
                        Enabled = ERenderParamUsage.Enabled,
                        Function = EComparison.Less,
                    },
                    //LineWidth = 1.0f,
                }
            })]);
    }

    private static void OnFinishedWorld(Task<(SceneNode? rootNode, IReadOnlyCollection<XRMaterial> materials, IReadOnlyCollection<XRMesh> meshes)> task)
    {
        if (task.IsCanceled || task.IsFaulted)
            return;

        (SceneNode? rootNode, IReadOnlyCollection<XRMaterial> materials, IReadOnlyCollection<XRMesh> meshes) = task.Result;
        rootNode?.GetTransformAs<Transform>()?.ApplyScale(new Vector3(0.01f));
    }
    private static void OnFinishedAvatarAsync(Task<(SceneNode? rootNode, IReadOnlyCollection<XRMaterial> materials, IReadOnlyCollection<XRMesh> meshes)> task)
    {
        if (task.IsCanceled || task.IsFaulted)
            return;

        (SceneNode? rootNode, IReadOnlyCollection<XRMaterial> materials, IReadOnlyCollection<XRMesh> meshes) = task.Result;
        OnFinishedImportingAvatar(rootNode);
    }

    private static void EnableTransformToolForNode(SceneNode? node)
    {
        if (node is null)
            return;
        
        //we have to wait for the scene node to be activated in the instance of the world before we can attach the transform tool
        void Edit(SceneNode x)
        {
            TransformTool3D.GetInstance(x.Transform);
            x.Activated -= Edit;
        }

        if (node.IsActiveInHierarchy && node.World is not null)
            TransformTool3D.GetInstance(node.Transform);
        else
            node.Activated += Edit;
    }
    
    //Hardcoded materials for testing until UI
    public static void MakeMaterial(XRMaterial mat, XRTexture[] textureList, List<TextureSlot> textures, string name)
    {
        //Debug.Out($"Making material for {name}: {string.Join(", ", textureList.Select(x => x?.Name ?? "<missing name>"))}");

        // Clear current shader list
        mat.Shaders.Clear();

        XRShader color = ShaderHelper.LitColorFragDeferred()!;
        XRShader albedo = ShaderHelper.LitTextureFragDeferred()!;
        XRShader albedoNormal = ShaderHelper.LitTextureNormalFragDeferred()!;
        XRShader albedoNormalMetallic = ShaderHelper.LitTextureNormalMetallicFragDeferred()!;
        XRShader albedoMetallic = ShaderHelper.LitTextureMetallicFragDeferred()!;
        XRShader albedoNormalRoughnessMetallic = ShaderHelper.LitTextureNormalRoughnessMetallicDeferred()!;
        XRShader albedoRoughness = ShaderHelper.LitTextureRoughnessFragDeferred()!;
        XRShader albedoMatcap = ShaderHelper.LitTextureMatcapDeferred()!;
        XRShader albedoEmissive = ShaderHelper.LitTextureEmissiveDeferred();

        switch (name)
        {
            case "Boots": // "BaseColor, Roughness, BaseColor, , Roughness"
                //textureList[0].Load3rdParty();
                mat.Shaders.Add(albedoRoughness);
                mat.Textures =
                [
                    textureList[0],
                    textureList[1],
                ];
                MakeDefaultParameters(mat);
                break;

            case "Metal": // "T_MainTex_D, T_MainTex_D"
                mat.Shaders.Add(color);
                MakeDefaultParameters(mat);
                mat.SetVector3("BaseColor", new Vector3(0.6f));
                mat.SetFloat("Roughness", 0.5f);
                mat.SetFloat("Metallic", 1.0f);
                mat.SetFloat("Specular", 1.0f);
                break;

            case "BackHair": // "HairEarTail Texture, HairEarTail Texture"
                mat.Shaders.Add(albedo);
                mat.Textures =
                [
                    textureList[0],
                ];
                MakeDefaultParameters(mat);
                break;

            case "Goth_Bunny_Straps": // "Arm matcaps, Arm matcaps"
                mat.Shaders.Add(albedoMatcap);
                mat.Textures =
                [
                    textureList.TryGet(0),
                ];
                MakeDefaultParameters(mat);
                break;

            case "black_1": // "T_MainTex_D, T_MainTex_D, "
                mat.Shaders.Add(color);
                MakeDefaultParameters(mat);
                break;

            case "Ears": // "ears emiss, ears emiss"
                mat.Shaders.Add(albedo);
                mat.Textures =
                [
                    textureList[0],
                ];
                MakeDefaultParameters(mat);
                break;

            case "Material #130": // no textures provided
                mat.Shaders.Add(color);
                MakeDefaultParameters(mat);
                break;

            case "Material #132": // "BLACK, NORMAL, METALIC, Regular_Roughness"
                mat.Shaders.Add(albedoNormalRoughnessMetallic);
                mat.Textures =
                [
                    textureList[0],
                    textureList[1],
                    textureList[2],
                    textureList[3],
                ];
                MakeDefaultParameters(mat);
                break;

            case "1": // "No Saturation 06 (1), No Saturation 06 (1)
                mat.Shaders.Add(albedo);
                mat.Textures = [textureList[0]];
                MakeDefaultParameters(mat);
                break;

            case "2": // "61, 61"
                mat.Shaders.Add(albedo);
                mat.Textures = [textureList[0]];
                MakeDefaultParameters(mat);
                break;

            case "Goth_Bunny_Thigh_Highs": // "generator5, generator5"
                mat.Shaders.Add(albedo);
                mat.Textures = [textureList[0]];
                MakeDefaultParameters(mat);
                break;

            case "LEFT_EYE": // "Eye_5, left eye by nanna, Eye_5"
                mat.Shaders.Add(albedo);
                mat.Textures = [textureList[0]];
                MakeDefaultParameters(mat);
                break;

            case "Back_hair": // "Hair1, hairglow-mono_emission, Hair1"
                mat.Shaders.Add(albedoEmissive);
                mat.Textures = [textureList[0], textureList[1]];
                MakeDefaultParameters(mat);
                break;

            case "New_002": // "lis_dlcu_029_02_base_PP1_K, lis_dlcu_029_02_base_PP1_K"
                mat.Shaders.Add(albedo);
                mat.Textures = [textureList[0]];
                MakeDefaultParameters(mat);
                break;

            case "Mat_Glow": // "T_MainTex_D, T_MainTex_D"
                mat.Shaders.Add(color);
                mat.Textures = [textureList[0]];
                MakeDefaultParameters(mat);
                break;

            case "FABRIC 1_FRONT_1830": // No textures given
                mat.Shaders.Add(color);
                MakeDefaultParameters(mat);
                break;

            case "Material #131": // "7"
                mat.Shaders.Add(albedo);
                mat.Textures = [textureList[0]];
                MakeDefaultParameters(mat);
                break;

            case "tail": // "T_MainTex_D, EM backhair, T_MainTex_D"
                mat.Shaders.Add(albedoEmissive);
                mat.Textures = [textureList[0], textureList[1]];
                MakeDefaultParameters(mat);
                break;

            case "hoodie": // "low poly_defaultMat.001_BaseColor.1001, low poly_defaultMat.001_Roughness.1001, "
                mat.Shaders.Add(albedoRoughness);
                mat.Textures = [textureList[0], textureList[1]];
                MakeDefaultParameters(mat);
                break;

            case "Face": // "googleface, googleface"
                mat.Shaders.Add(albedo);
                mat.Textures = [textureList[0]];
                MakeDefaultParameters(mat);
                break;

            case "Material #98": // "Shorts_bake_Merge, Shorts_Normal_OpenGL"
                mat.Shaders.Add(albedoNormal);
                mat.Textures = [textureList[0]];
                MakeDefaultParameters(mat);
                break;

            case "Material.002": // Empty setup
                mat.Shaders.Add(color);
                MakeDefaultParameters(mat);
                break;

            case "metal.002": // Empty setup
                mat.Shaders.Add(color);
                MakeDefaultParameters(mat);
                break;

            case "Body": // "Body, Body"
                mat.Shaders.Add(albedo);
                mat.Textures = [textureList[0]];
                MakeDefaultParameters(mat);
                break;

            case "RIGHT_EYE": // "Eye_5, right eye by nanna, Eye_5"
                mat.Shaders.Add(color);
                mat.Textures = [textureList[0]];
                MakeDefaultParameters(mat);
                break;

            case "TEETH___SOCK": // "T_MainTex_D, T_MainTex_D"
                mat.Shaders.Add(color);
                MakeDefaultParameters(mat);
                break;

            default:
                // Default material setup
                mat.Shaders.Add(color);
                MakeDefaultParameters(mat);
                break;
        }
        mat.Name = name;
        // Set a default render pass (opaque deferred lighting in this example)
        mat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferredLit;
    }
    private static XRTexture2D MakeTexture(string path)
    {
        Dictionary<string, string> pathRemap = new()
        {
            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Shoes\\Adidas_Superstar\\BLACK\\BLACK.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Shoes\\Adidas_Superstar\\BLACK\\BLACK.png"
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Shoes\\Adidas_Superstar\\NORMAL.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Shoes\\Adidas_Superstar\\NORMAL.png"
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Shoes\\Adidas_Superstar\\METALIC.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Shoes\\Adidas_Superstar\\METALIC.png"
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Shoes\\Adidas_Superstar\\Regular_Roughness.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Shoes\\Adidas_Superstar\\Regular_Roughness.png"
            },

            {
                "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\1\\T_MainTex_D.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\T_MainTex_D.png"
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Shoes\\Leather High Boots Extended\\Leather High Boots Extended\\2k_base\\BaseColor.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Shoes\\Leather High Boots Extended\\Leather High Boots Extended\\2k_base\\BaseColor.png"
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Shoes\\Leather High Boots Extended\\Leather High Boots Extended\\2k_base\\Roughness.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Shoes\\Leather High Boots Extended\\Leather High Boots Extended\\2k_base\\Roughness.png"
            },

            {
                "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\T_MainTex_D.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\T_MainTex_D.png"
            },

            {
                "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\generator5.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\generator5.png"
            },

            {
                "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\ears emiss.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\ears emiss.png"
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Underwear\\Female Bombshell Bra\\Female Bombshell Bra\\Texture_Valentines Bra Black Lace trans.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Underwear\\Female Bombshell Bra\\Female Bombshell Bra\\Texture_Valentines Bra Black Lace trans.png"
            },

            {
                "D:\\Documents\\Avatar2\\Assets\\Zafira Model By Luuy\\Tex\\lis_dlcu_029_02_base_PP1_K.png",
                ""
            },

            {
                "D:\\Documents\\Avatar2\\Assets\\Zafira Model By Luuy\\Tex\\Hair1.png",
                ""
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Tops\\Shawty hoodie - Zinpia\\7.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Tops\\Shawty hoodie - Zinpia\\7.png"
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Hair\\BedHead v2 by Nessy\\Textures\\61.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Hair\\HairPack WetCat\\Textures\\61.png"
            },

            {
                "D:\\Documents\\Avatar2\\Assets\\Zafira Model By Luuy\\Tex\\hairglow-mono_emission.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\R\\Avatar-Lovelylove-Asset-bundle-2.file_ad87b39f-eb28-49ba-bae7-ab7bc0f312fc.1.vrca\\Assets\\Texture2D\\hairglow-mono_emission 1.png"
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\Misc\\Main\\X\\Val\\Jax.fbm\\Eye_5.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\Eye_5.png"
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\Misc\\Main\\X\\Val\\Texture2D\\Body.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Texture2D\\Body.png" },

            {
                "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\left eye by nanna.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\left eye by nanna.png"
            },

            {
                "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\EM backhair.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\EM backhair.png"
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Hair\\Bedhead by Nessy\\Textures\\No Saturation 01.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Textures\\Cici Hair\\No Saturation 01.png"
            },

            {
                "D:\\Documents\\Avatar2\\Assets\\Main\\Avatars\\MAD LOVE\\Materials\\Textures\\HairEarTail Texture.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Texture2D\\HairEarTail Texture.png"
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\Misc\\Main\\X\\Val\\Jax.fbm\\googleface.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\googleface.png"
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Tops\\panda split dye hoodie by clally#6969\\split dye hoodie for panda.fbm\\low poly_defaultMat.001_BaseColor.1001.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Tops\\panda split dye hoodie by clally#6969\\split dye hoodie for panda.fbm\\low poly_defaultMat.001_BaseColor.1001.png"
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Tops\\panda split dye hoodie by clally#6969\\split dye hoodie for panda.fbm\\low poly_defaultMat.001_Roughness.1001.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Tops\\panda split dye hoodie by clally#6969\\split dye hoodie for panda.fbm\\low poly_defaultMat.001_Roughness.1001.png"
            },

            {
                "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\Arm matcaps.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\Arm matcaps.png"
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Hair\\BedHead v2 by Nessy\\Textures\\51.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Hair\\Jen by Nessy\\Textures\\51.png"
            },

            {
                "D:\\Documents\\Avatar2\\Assets\\Main\\Rips\\worker_lXgVip0x3vX06jqcB6_zGr9UoSbJw1C1PGuPZ-tmp1\\Assets\\Texture2D\\T_MainTex_D.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\T_MainTex_D.png"
            },

            {
                "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Hair\\Bedhead by Nessy\\Textures\\No Saturation 06 (1).png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Textures\\Cici Hair\\No Saturation 06.png"
            },

            {
                "L:\\CustomAvatars2\\Assets\\VRChat Assets\\Pants\\Shorts by Zeit\\Shorts_By_Zeit_rar\\Shorts_bake_Merge.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Jax VRM Export\\Jax VRM2.Textures\\Shorts_bake_Merge.png"
            },

            {
                "L:\\CustomAvatars2\\Assets\\VRChat Assets\\Pants\\Shorts by Zeit\\Shorts_By_Zeit_rar\\Shorts_Normal_OpenGL.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\! Saint By Hate\\Textures\\Shorts_Normal_OpenGL.png"
            },

            {
                "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\right eye by nanna.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\right eye by nanna.png"
            },

            {
                "L:\\CustomAvatars2\\Assets\\VRChat Assets\\Pants\\Shorts by Zeit\\Shorts_By_Zeit_rar\\Shorts_bake_Merge_metal.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\!Textures\\Split By Yumiko\\Shorts_bake_Merge_metal.png"
            },

            {
                "L:\\CustomAvatars2\\Assets\\VRChat Assets\\Pants\\Shorts by Zeit\\Shorts_By_Zeit_rar\\Metal_Normal_OpenGL.png",
                "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\JaxVRCToolkit_Autogenerated\\AshOld\\Textures\\Metal_Normal_OpenGL.png"
            }
        };

        if (!File.Exists(path) && pathRemap.TryGetValue(path, out string? newPath) && !string.IsNullOrEmpty(newPath))
            path = newPath;

        var tex = Engine.Assets.Load<XRTexture2D>(path);
        if (tex is null)
        {
            Debug.Out($"Failed to load texture: {path}");
            tex = new XRTexture2D()
            {
                Name = Path.GetFileNameWithoutExtension(path),
                MagFilter = ETexMagFilter.Linear,
                MinFilter = ETexMinFilter.Linear,
                UWrap = ETexWrapMode.Repeat,
                VWrap = ETexWrapMode.Repeat,
                AlphaAsTransparency = true,
                AutoGenerateMipmaps = true,
                Resizable = true,
            };
        }
        else
        {
            //Debug.Out($"Loaded texture: {path}");
            tex.MagFilter = ETexMagFilter.Linear;
            tex.MinFilter = ETexMinFilter.Linear;
            tex.UWrap = ETexWrapMode.Repeat;
            tex.VWrap = ETexWrapMode.Repeat;
            tex.AlphaAsTransparency = true;
            tex.AutoGenerateMipmaps = true;
            tex.Resizable = false;
            tex.SizedInternalFormat = ESizedInternalFormat.Rgba8;
        }
        return tex;
    }
    private static void MakeDefaultParameters(XRMaterial mat)
    {
        mat.Parameters =
        [
            new ShaderVector3(new Vector3(1.0f, 1.0f, 1.0f), "BaseColor"),
            new ShaderFloat(1.0f, "Opacity"),
            new ShaderFloat(1.0f, "Roughness"),
            new ShaderFloat(0.0f, "Metallic"),
            new ShaderFloat(0.0f, "Specular"),
            new ShaderFloat(0.0f, "Emission"),
        ];
    }
}