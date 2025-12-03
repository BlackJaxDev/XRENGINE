using MagicPhysX;
using Silk.NET.OpenAL;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Components.Physics;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Transforms;
using XREngine.Components.VR;
using XREngine.Data.Colors;
using XREngine.Data.Components.Scene;
using XREngine.Rendering;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using static XREngine.Scene.Transforms.RigidBodyTransform;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static class Pawns
    {
        public static SceneNode CreatePlayerPawn(bool setUI, bool isServer, SceneNode rootNode)
        {
            SceneNode? characterPawnModelParentNode = null;
            if (Toggles.VRPawn)
            {
                if (Toggles.Locomotion)
                {
                    characterPawnModelParentNode = CreateCharacterVRPawn(rootNode, setUI, out var pawn, out _, out var leftHand, out var rightHand);
                    if (Toggles.AllowEditingInVR || Toggles.AddCameraVRPickup)
                    {
                        SceneNode cameraNode = CreateCamera(rootNode, out var camComp);
                        var pawn2 = CreateDesktopCamera(cameraNode, isServer, Toggles.AllowEditingInVR && !Toggles.AddCameraVRPickup, Toggles.AddCameraVRPickup, false);
                        if (setUI)
                            UserInterface.CreateEditorUI(rootNode, camComp, pawn2);
                    }
                    else if (setUI) //TODO: render ui on left or right controller when opened
                        UserInterface.CreateEditorUI(characterPawnModelParentNode, null, pawn);
                }
                else
                    CreateFlyingVRPawn(rootNode, setUI);
            }
            else if (Toggles.Locomotion)
                characterPawnModelParentNode = CreateDesktopCharacterPawn(rootNode, setUI);
            else
            {
                SceneNode cameraNode = CreateCamera(rootNode, out var camComp);
                var pawn = CreateDesktopCamera(cameraNode, isServer, true, false, true);
                if (setUI)
                    UserInterface.CreateEditorUI(rootNode, camComp!, pawn);
            }

            return characterPawnModelParentNode;
        }

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
            if (!Toggles.AllowEditingInVR)
                characterComp.EnqueuePossessionByLocalPlayer(ELocalPlayerIndex.One);

            SceneNode localRotationNode = vrPlayspaceNode.NewChild("LocalRotationNode");
            characterComp.IgnoreViewTransformPitch = true;
            characterComp.ViewRotationTransform = localRotationNode.GetTransformAs<Transform>(true)!;

            //The foot node will be used to position the playspace node on the floor, because the center of the capsule is not the same as the foot position
            var footNode = localRotationNode.NewChild("Foot Position Node");
            var footTfm = footNode.SetTransform<Transform>();
            footTfm.Translation = new Vector3(0.0f, -movementComp.HalfHeight, 0.0f);
            footTfm.SaveBindState();

            //The playspace node will be shifted around on XZ to keep a certain tracked device centered on the foot node
            var playspaceNode = footNode.NewChild("Playspace Node");
            var playspaceTfm = playspaceNode.SetTransform<Transform>();

            CreateVRDevices(
                setUI,
                out hmdTfm,
                out leftTfm,
                out rightTfm,
                vrPlayspaceNode,
                characterComp,
                vrInput,
                playspaceNode);

            //This is the node used as the parent for the avatar model
            return footNode;
        }

        private static void CreateVRDevices(
            bool setUI,
            out VRHeadsetTransform hmdTfm,
            out VRControllerTransform leftTfm,
            out VRControllerTransform rightTfm,
            SceneNode vrPlayspaceNode,
            CharacterPawnComponent characterComp,
            VRPlayerInputSet vrInput,
            SceneNode playspaceNode)
        {
            PawnComponent? refPawn = characterComp;
            characterComp.InputOrientationTransform = AddHeadsetNode(out hmdTfm, out _, playspaceNode, setUI, out var canvas, ref refPawn).Transform;

            AddHandControllerNode(out leftTfm, playspaceNode, true);
            AddHandControllerNode(out rightTfm, playspaceNode, false);

            if (canvas is not null)
            {
                void ShowDesktopMenu()
                    => UserInterface.ShowMenu(canvas, true, vrPlayspaceNode.Transform);

                characterComp.PauseToggled += ShowDesktopMenu;

                void ShowVRMenu(bool leftHand)
                    => UserInterface.ShowMenu(canvas, false, leftHand ? vrInput.LeftHandTransform : vrInput.RightHandTransform);

                vrInput.PauseToggled += ShowVRMenu;
            }

            var coll = AddTrackerCollectionNode(playspaceNode);

            if (Toggles.EmulatedVRPawn)
            {
                var hmdDD = hmdTfm.SceneNode!.AddComponent<DebugDrawComponent>()!;
                hmdDD.AddSphere(0.01f, Vector3.Zero, ColorF4.DarkRed, false);

                var lf = coll.AddManualTracker("Left Foot");
                lf.LocalMatrixOffset = Matrix4x4.CreateTranslation(-0.2f, 0, 0);
                var lfDD = lf.SceneNode!.AddComponent<DebugDrawComponent>()!;
                lfDD.AddSphere(0.01f, Vector3.Zero, ColorF4.DarkTeal, false);

                var rf = coll.AddManualTracker("Right Foot");
                rf.LocalMatrixOffset = Matrix4x4.CreateTranslation(0.2f, 0, 0);
                var rfDD = rf.SceneNode!.AddComponent<DebugDrawComponent>()!;
                rfDD.AddSphere(0.01f, Vector3.Zero, ColorF4.DarkTeal, false);

                var hip = coll.AddManualTracker("Hip");
                hip.LocalMatrixOffset = Matrix4x4.CreateTranslation(0, 0.4f, 0);
                var hipDD = hip.SceneNode!.AddComponent<DebugDrawComponent>()!;
                hipDD.AddSphere(0.01f, Vector3.Zero, ColorF4.DarkTeal, false);
            }

            vrInput.LeftHandTransform = leftTfm;
            vrInput.RightHandTransform = rightTfm;
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
            //movementComp.JumpHoldForce = 1.0f;
            //movementComp.GravityOverride = new Vector3(0.0f, -1.0f, 0.0f);
            movementComp.InputLerpSpeed = 0.9f;
        }

        private static void AddHandControllerNode(out VRControllerTransform controllerTfm, SceneNode parentNode, bool left)
        {
            SceneNode controllerNode = parentNode.NewChild($"VR{(left ? "Left" : "Right")}ControllerNode");

            controllerTfm = controllerNode.SetTransform<VRControllerTransform>();
            controllerTfm.LeftHand = left;

            if (Toggles.EmulatedVRPawn)
            {
                var debugComp = controllerNode.AddComponent<DebugDrawComponent>()!;
                debugComp.AddSphere(0.01f, Vector3.Zero, ColorF4.Black, false);
            }
            else
            {
                var modelComp = controllerNode.AddComponent<VRControllerModelComponent>()!;
                modelComp.LeftHand = left;
            }
        }
        private static void CreateFlyingVRPawn(SceneNode rootNode, bool setUI)
        {
            SceneNode vrPlayspaceNode = new(rootNode) { Name = "VRPlayspaceNode" };
            var playspaceTfm = vrPlayspaceNode.SetTransform<Transform>();
            PawnComponent? pawn = null;
            AddHeadsetNode(out _, out _, vrPlayspaceNode, setUI, out _, ref pawn);
            AddHandControllerNode(out _, vrPlayspaceNode, true);
            AddHandControllerNode(out _, vrPlayspaceNode, false);
            AddTrackerCollectionNode(vrPlayspaceNode);
            VRPlayerInputSet? input = pawn?.SceneNode?.AddComponent<VRPlayerInputSet>()!;
        }

        private static VRTrackerCollectionComponent AddTrackerCollectionNode(SceneNode vrPlayspaceNode)
        {
            vrPlayspaceNode.NewChild(out VRTrackerCollectionComponent coll, "VRTrackerCollectionNode");
            return coll;
        }

        private static SceneNode AddHeadsetNode(
            out VRHeadsetTransform hmdTfm,
            out VRHeadsetComponent hmdComp,
            SceneNode parentNode,
            bool setUI,
            out UICanvasComponent? canvas,
            ref PawnComponent? pawn)
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

            if (!Toggles.AllowEditingInVR)
                AddVRFirstPersonDesktopView(ref pawn, vrHeadsetNode);
            
            return vrHeadsetNode;
        }

        private static void AddVRFirstPersonDesktopView(ref PawnComponent pawn, SceneNode parentNode)
        {
            SceneNode firstPersonViewNode = new(parentNode) { Name = "FirstPersonViewNode" };
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

            if (!(Toggles.VRPawn && Toggles.AllowEditingInVR) && Toggles.Microphone && !(Toggles.ImportAnimatedModel && Toggles.AttachMicToAnimatedModel))
                Audio.AttachMicTo(cameraNode, out _, out _, out _);

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

        private static SceneNode CreateDesktopCharacterPawn(SceneNode rootNode, bool setUI)
        {
            SceneNode characterNode = new(rootNode, "Player");
            var characterTfm = characterNode.SetTransform<RigidBodyTransform>();
            characterTfm.InterpolationMode = EInterpolationMode.Interpolate;

            var movementComp = characterNode.AddComponent<CharacterMovement3DComponent>()!;
            InitMovement(movementComp);

            var footNode = characterNode.NewChild("Foot Position Node");
            var footTfm = footNode.SetTransform<Transform>();
            footTfm.Translation = new Vector3(0.0f, -movementComp.HalfHeight, 0.0f);
            //footTfm.Scale = new Vector3(movementComp.StandingHeight);
            footTfm.SaveBindState();

            //create node to translate camera up half the height of the character
            SceneNode cameraOffsetNode = new(footNode, "Camera Offset");
            var cameraOffsetTfm = cameraOffsetNode.SetTransform<Transform>();
            cameraOffsetTfm.Translation = new Vector3(0.0f, 0.0f, 0.0f);

            SceneNode cameraParentNode;
            if (Toggles.ThirdPersonPawn)
            {
                //Create camera boom with sphere shapecast
                cameraParentNode = cameraOffsetNode.NewChildWithTransform<BoomTransform>(out var boomTfm, "3rd Person Camera Boom");
                boomTfm.MaxLength = 10.0f;
                boomTfm.ZoomOutSpeed = 5.0f;
            }
            else
                cameraParentNode = cameraOffsetNode;

            SceneNode cameraNode = CreateCamera(cameraParentNode, out CameraComponent? camComp, null, !Toggles.ThirdPersonPawn);

            var listener = cameraNode.AddComponent<AudioListenerComponent>("Desktop Character Listener")!;
            listener.Gain = 1.0f;
            listener.DistanceModel = DistanceModel.InverseDistance;
            listener.DopplerFactor = 0.5f;
            listener.SpeedOfSound = 343.3f;

            var characterComp = characterNode.AddComponent<CharacterPawnComponent>("TestPawn")!;
            characterComp.CameraComponent = camComp;
            characterComp.InputOrientationTransform = cameraNode.Transform;
            characterComp.ViewRotationTransform = cameraOffsetTfm;

            if (camComp is not null && setUI)
            {
                UICanvasComponent canvas = UserInterface.CreateEditorUI(rootNode, camComp);
                canvas.IsActive = false;
                characterComp.PauseToggled += () => UserInterface.ShowMenu(canvas, true, rootNode.Transform);
            }

            characterComp.EnqueuePossessionByLocalPlayer(ELocalPlayerIndex.One);

            return footNode;
        }

        #endregion

        private static SceneNode CreateCamera(SceneNode parentNode, out CameraComponent? camComp, float? smoothed = 50.0f, bool localSmoothing = true)
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

        public static void InitializeLocomotion(
            SceneNode rootNode,
            HumanoidComponent humanComp,
            HeightScaleComponent heightScale,
            VRIKSolverComponent? vrIKSolver)
        {
            SceneNode rigidBodyNode;
            const string EyeLNodeName = "Eye_L";
            const string EyeRNodeName = "Eye_R";
            const string faceNodeName = "Face";
            var footNode = rootNode.Parent!;
            if (Toggles.VRPawn)
            {
                var rotationNode = footNode.Parent!;
                rigidBodyNode = rotationNode.Parent!;
                heightScale.CharacterMovementComponent = rigidBodyNode.GetComponent<CharacterMovement3DComponent>()!;

                var player = rigidBodyNode.AddComponent<VRPlayerCharacterComponent>()!;
                player.HeightScaleComponent = heightScale;
                player.IKSolver = vrIKSolver;
                player.HumanoidComponent = humanComp;
                player.EyeLBoneName = EyeLNodeName;
                player.EyeRBoneName = EyeRNodeName;
                player.EyesModelResolveName = faceNodeName;

                VRPlayerInputSet input = rigidBodyNode.GetComponent<VRPlayerInputSet>()!;

                void EndCalibration(bool enabled)
                {
                    if (player.IsCalibrating)
                        player.EndCalibration();
                    else
                        player.BeginCalibration();
                }

                input.IsMutedChanged += EndCalibration;

                if (Toggles.EmulatedVRPawn)
                {
                    var playspaceNode = footNode.FirstChild!;
                    var trackerColl = playspaceNode.LastChild!.GetComponent<VRTrackerCollectionComponent>()!;

                    var extOpt = rigidBodyNode.AddComponent<ExternalOptionalInputSetComponent>()!;
                    extOpt.OnRegisterInput += RegisterEmulatorActions;

                    //Crazy band-aid to register these
                    if (Toggles.AllowEditingInVR)
                        Engine.State.MainPlayer.ControlledPawn?.OptionalInputSets.Add(extOpt);

                    void RegisterEmulatorActions(Input.Devices.InputInterface inputSet)
                    {
                        inputSet.RegisterKeyEvent(Input.Devices.EKey.T, Input.Devices.EButtonInputType.Pressed, AddTracker);
                        inputSet.RegisterKeyEvent(Input.Devices.EKey.Y, Input.Devices.EButtonInputType.Pressed, player.EndCalibration);
                        inputSet.RegisterKeyEvent(Input.Devices.EKey.Number0, Input.Devices.EButtonInputType.Pressed, SelectHMD);
                        inputSet.RegisterKeyEvent(Input.Devices.EKey.Number9, Input.Devices.EButtonInputType.Pressed, SelectLeftController);
                        inputSet.RegisterKeyEvent(Input.Devices.EKey.Number8, Input.Devices.EButtonInputType.Pressed, SelectRightController);
                        inputSet.RegisterKeyEvent(Input.Devices.EKey.Number7, Input.Devices.EButtonInputType.Pressed, SelectLeftFootTracker);
                        inputSet.RegisterKeyEvent(Input.Devices.EKey.Number6, Input.Devices.EButtonInputType.Pressed, SelectRightFootTracker);
                        inputSet.RegisterKeyEvent(Input.Devices.EKey.Number5, Input.Devices.EButtonInputType.Pressed, SelectHipTracker);
                        inputSet.RegisterKeyEvent(Input.Devices.EKey.Number4, Input.Devices.EButtonInputType.Pressed, AutoSetTrackers);
                        inputSet.RegisterKeyEvent(Input.Devices.EKey.Minus, Input.Devices.EButtonInputType.Pressed, SelectRoot);
                    }
                    void AutoSetTrackers()
                    {
                        VRHeadsetComponent? hmd = VRHeadsetComponent.Instance;
                        VRControllerTransform? rightController = input.RightHandTransform;
                        VRControllerTransform? leftController = input.LeftHandTransform;

                        float height = Engine.VRState.ModelHeight * Engine.VRState.ModelToRealWorldHeightRatio;
                        Vector3 headPos = humanComp.Head!.Node!.Transform.WorldTranslation;
                        headPos.Y = 0.0f; //Set the head position to the ground level
                        headPos.Y += height;
                        Vector3 headTranslation = (player.GetHeightScaleComponent()?.ScaledToRealWorldEyeOffsetFromHead ?? Vector3.Zero) + headPos;

                        hmd?.Transform?.DeriveWorldMatrix(Matrix4x4.CreateWorld(headTranslation, Globals.Forward, Globals.Up), false);
                        rightController?.DeriveWorldMatrix(humanComp.Right.Wrist.Node!.Transform.WorldMatrix, false);
                        leftController?.DeriveWorldMatrix(humanComp.Left.Wrist.Node!.Transform.WorldMatrix, false);

                        VRTrackerTransform? hipTracker = trackerColl.GetTrackerByNodeName("Hip");
                        VRTrackerTransform? rightFootTracker = trackerColl.GetTrackerByNodeName("Right Foot");
                        VRTrackerTransform? leftFootTracker = trackerColl.GetTrackerByNodeName("Left Foot");

                        TransformBase hipTfm = humanComp.Hips.Node!.Transform;
                        TransformBase lfTfm = humanComp.Left.Foot.Node!.Transform;
                        TransformBase rfTfm = humanComp.Right.Foot.Node!.Transform;

                        hipTracker?.DeriveWorldMatrix(hipTfm.WorldMatrix, false);
                        rightFootTracker?.DeriveWorldMatrix(rfTfm.WorldMatrix, false);
                        leftFootTracker?.DeriveWorldMatrix(lfTfm.WorldMatrix, false);
                    }
                    void SelectHipTracker()
                    {
                        VRTrackerTransform? hipTracker = trackerColl.GetTrackerByNodeName("Hip");
                        if (hipTracker is not null)
                            Selection.SceneNode = hipTracker.SceneNode;
                    }
                    void SelectRightFootTracker()
                    {
                        VRTrackerTransform? rightFootTracker = trackerColl.GetTrackerByNodeName("Right Foot");
                        if (rightFootTracker is not null)
                            Selection.SceneNode = rightFootTracker.SceneNode;
                    }
                    void SelectLeftFootTracker()
                    {
                        VRTrackerTransform? leftFootTracker = trackerColl.GetTrackerByNodeName("Left Foot");
                        if (leftFootTracker is not null)
                            Selection.SceneNode = leftFootTracker.SceneNode;
                    }
                    void SelectRightController()
                    {
                        VRControllerTransform? rightController = input.RightHandTransform;
                        if (rightController is not null)
                            Selection.SceneNode = rightController.SceneNode;
                    }
                    void SelectLeftController()
                    {
                        VRControllerTransform? leftController = input.LeftHandTransform;
                        if (leftController is not null)
                            Selection.SceneNode = leftController.SceneNode;
                    }
                    void SelectHMD()
                    {
                        VRHeadsetComponent? hmd = VRHeadsetComponent.Instance;
                        if (hmd is not null)
                            Selection.SceneNode = hmd.SceneNode;
                    }
                    void SelectRoot()
                    {
                        Selection.SceneNode = rootNode;
                    }
                    void AddTracker()
                    {

                    }
                }
            }
            else
            {
                var eyeOffsetNode = footNode.FirstChild!;
                rigidBodyNode = footNode.Parent!;
                heightScale.CharacterMovementComponent = rigidBodyNode.GetComponent<CharacterMovement3DComponent>()!;

                ModelComponent? faceModel = humanComp.SceneNode.FindDescendant(x => x.Name?.Contains(faceNodeName, StringComparison.InvariantCultureIgnoreCase) ?? false)?.GetComponent<ModelComponent>();
                if (faceModel is not null)
                {
                    void FaceModel_ModelChanged()
                    {
                        heightScale.MeasureAvatarHeight();
                        heightScale.CalculateEyeOffsetFromHead(faceModel, EyeLNodeName, EyeRNodeName);
                        eyeOffsetNode!.GetTransformAs<Transform>(true)!.Translation = VRPlayerCharacterComponent.GetScaledToRealWorldHeadOffsetFromAvatarRoot(humanComp) + heightScale.ScaledToRealWorldEyeOffsetFromHead;
                    }
                    faceModel.ModelChanged += FaceModel_ModelChanged;
                }
            }
        }
    }
}