using MagicPhysX;
using Silk.NET.OpenAL;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Colors;
using XREngine.Data.Components.Scene;
using XREngine.Rendering;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Components.Physics;
using XREngine.Scene.Components.VR;
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
            characterTfm.InterpolationMode = EInterpolationMode.Discrete;

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
                hmdTfm.SceneNode!.AddComponent<DebugDrawComponent>()!.AddSphere(0.15f, Vector3.Zero, ColorF4.DarkRed, false);

                var lf = coll.AddManualTracker("Left Foot");
                lf.LocalMatrixOffset = Matrix4x4.CreateTranslation(-0.2f, 0, 0);
                lf.SceneNode!.AddComponent<DebugDrawComponent>()!.AddSphere(0.05f, Vector3.Zero, ColorF4.DarkTeal, false);

                var rf = coll.AddManualTracker("Right Foot");
                rf.LocalMatrixOffset = Matrix4x4.CreateTranslation(0.2f, 0, 0);
                rf.SceneNode!.AddComponent<DebugDrawComponent>()!.AddSphere(0.05f, Vector3.Zero, ColorF4.DarkTeal, false);

                var hip = coll.AddManualTracker("Hip");
                hip.LocalMatrixOffset = Matrix4x4.CreateTranslation(0, 0.4f, 0);
                hip.SceneNode!.AddComponent<DebugDrawComponent>()!.AddSphere(0.05f, Vector3.Zero, ColorF4.DarkTeal, false);
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
            movementComp.JumpSpeed = 1.0f;
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
                debugComp.AddSphere(0.1f, Vector3.Zero, ColorF4.Black, false);
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
                UICanvasComponent canvas = UserInterface.CreateEditorUI(characterNode, camComp);
                canvas.IsActive = false;
                characterComp.PauseToggled += () => UserInterface.ShowMenu(canvas, true, characterNode.Transform);
            }

            characterComp.EnqueuePossessionByLocalPlayer(ELocalPlayerIndex.One);

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
    }
}