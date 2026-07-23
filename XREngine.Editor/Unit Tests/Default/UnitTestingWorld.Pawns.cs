using MagicPhysX;
using System.Numerics;
using XREngine.Audio;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Components.Physics;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Transforms;
using XREngine.Components.VR;
using XREngine.Data.Colors;
using XREngine.Data.Components.Scene;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Scene.Physics.Physx;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Transforms;
using static XREngine.Scene.Transforms.RigidBodyTransform;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    public static class Pawns
    {
        private const string EditorViewCameraName = "Editor View";
        private const string CameraVRPickupName = "VR Pickup Camera";
        private const float FirstPersonDesktopHorizontalFieldOfView = 50.0f;
        private const float CameraVRPickupDirectionEpsilonSquared = 1.0e-8f;
        private static readonly Vector3 CameraVRPickupInitialPosition = new(0.0f, 1.7f, 4.5f);
        private static readonly Vector3 CameraVRPickupInitialLookAt = new(0.0f, 1.4f, 0.0f);

        public static SceneNode? CreatePlayerPawn(bool setUI, bool isServer, SceneNode rootNode)
        {
            SceneNode? characterPawnModelParentNode = null;
            if (Toggles.VRPawn)
            {
                if (Toggles.Locomotion)
                {
                    characterPawnModelParentNode = CreateCharacterVRPawn(rootNode, setUI, out _, out _, out _, out _);
                    CreateVrDesktopEditorCamera(rootNode, setUI, isServer);
                    CreateCameraVRPickup(rootNode, setUI);
                }
                else
                {
                    CreateFlyingVRPawn(rootNode, setUI);
                    CreateVrDesktopEditorCamera(rootNode, setUI, isServer);
                    CreateCameraVRPickup(rootNode, setUI);
                }
            }
            else if (Toggles.Locomotion)
                characterPawnModelParentNode = CreateDesktopCharacterPawn(rootNode, setUI);
            else
            {
                SceneNode cameraNode = CreateCamera(rootNode, out var camComp, null);
                var pawn = CreateDesktopCamera(cameraNode, isServer, true, true);
                if (setUI)
                    UserInterface.CreateEditorUI(rootNode, camComp!, pawn);
            }

            return characterPawnModelParentNode;
        }

        private static void CreateVrDesktopEditorCamera(SceneNode rootNode, bool setUI, bool isServer)
        {
            if (!Toggles.AllowEditingInVR)
                return;

            SceneNode cameraNode = CreateCamera(rootNode, out var camComp, null);
            var pawn = CreateDesktopCamera(cameraNode, isServer, flyable: true, addListener: false);
            if (setUI)
                UserInterface.CreateEditorUI(rootNode, camComp, pawn);
        }

        private static void CreateCameraVRPickup(SceneNode rootNode, bool setUI)
        {
            if (!Toggles.AddCameraVRPickup)
                return;

            SceneNode cameraNode = CreateCamera(rootNode, out var camComp, null, cameraName: CameraVRPickupName);
            cameraNode.SuppressTransformDebugLineAndPoint = false;
            cameraNode.SuppressTransformTools = false;
            var (initialPosition, initialRotation) = GetInitialCameraVRPickupPose();
            DynamicRigidBodyComponent pickupBody = AddCameraPickupPhysicsBody(cameraNode, initialPosition, initialRotation);
            ApplyCameraVRPickupPose(cameraNode, pickupBody, initialPosition, initialRotation);

            if (camComp is not null)
                ConfigureCameraVRPickupPreviewCamera(camComp);

            if (setUI && camComp is not null)
                UserInterface.CreateCameraPreviewOverlay(camComp, CameraVRPickupName);
        }

        private static void ConfigureCameraVRPickupPreviewCamera(CameraComponent camera)
        {
            camera.AntiAliasingModeOverride = EAntiAliasingMode.None;
            camera.Camera.MsaaSampleCountOverride = 1u;
            camera.Camera.OutputHDROverride = false;
            camera.Camera.TsrRenderScaleOverride = 1.0f;
        }

        private static (Vector3 Position, Quaternion Rotation) GetInitialCameraVRPickupPose()
        {
            Vector3 direction = CameraVRPickupInitialLookAt - CameraVRPickupInitialPosition;
            if (direction.LengthSquared() < CameraVRPickupDirectionEpsilonSquared)
                direction = Globals.Forward;
            else
                direction = Vector3.Normalize(direction);

            Vector3 up = Globals.Up;
            if (MathF.Abs(Vector3.Dot(direction, up)) > 0.999f)
                up = Globals.Right;

            Quaternion rotation = Quaternion.CreateFromRotationMatrix(
                Matrix4x4.CreateWorld(CameraVRPickupInitialPosition, direction, up));

            return (CameraVRPickupInitialPosition, rotation);
        }

        private static void ApplyCameraVRPickupPose(
            SceneNode cameraNode,
            DynamicRigidBodyComponent pickupBody,
            Vector3 position,
            Quaternion rotation)
        {
            RigidBodyTransform rigidBodyTransform = pickupBody.RigidBodyTransform;
            rigidBodyTransform.SetPositionAndRotation(position, rotation);

            if (pickupBody.RigidBody is PhysxDynamicRigidBody body)
            {
                body.SetTransform(position, rotation, wake: false);
                body.SetLinearVelocity(Vector3.Zero);
                body.SetAngularVelocity(Vector3.Zero);
            }

            Debug.Rendering(
                "[UnitTestingWorld] Camera VR pickup pose applied node='{0}' position={1} rotation={2} transformPosition={3} transformRotation={4}",
                cameraNode.Name ?? "<null>",
                position,
                rotation,
                rigidBodyTransform.Position,
                rigidBodyTransform.Rotation);
        }

        public static MeshEditingPawnComponent CreateMeshEditingPawn(SceneNode rootNode, bool setUI)
        {
            SceneNode cameraNode = CreateCamera(rootNode, out CameraComponent? camComp, 45.0f, true);

            var listener = cameraNode.AddComponent<AudioListenerComponent>("Mesh Editing Listener")!;
            listener.Gain = 1.0f;
            listener.DistanceModel = EDistanceModel.InverseDistance;
            listener.DopplerFactor = 0.5f;
            listener.SpeedOfSound = 343.3f;

            var pawn = cameraNode.AddComponent<MeshEditingPawnComponent>()!;
            pawn.Name = "Mesh Editing Pawn";
            pawn.EnqueuePossessionByLocalPlayer(ELocalPlayerIndex.One);
            ConfigureEditorViewCamera(rootNode, cameraNode);

            if (setUI && camComp is not null)
                UserInterface.CreateEditorUI(rootNode, camComp);

            return pawn;
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

            float spawnY = Toggles.CharacterControllerCapsuleTranslationY ?? (movementComp.HalfHeight + 0.01f);
            characterTfm.SetPositionAndRotation(new Vector3(0.0f, spawnY, 0.0f), Quaternion.Identity);

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

            if (Toggles.SceneOnlyVRPawn)
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

        private static void VrInput_HandGrabbed(VRPlayerInputSet sender, IAbstractDynamicRigidBody item, bool left)
        {

        }

        private static void ChangeHighlight(IAbstractDynamicRigidBody? prev, IAbstractDynamicRigidBody? current)
        {
            DefaultRenderPipeline.SetHighlighted(prev, false);
            DefaultRenderPipeline.SetHighlighted(current, true);
        }

        private static void OnLeftHandOverlapChanged(VRPlayerInputSet set, IAbstractDynamicRigidBody? prev, IAbstractDynamicRigidBody? current)
            => ChangeHighlight(prev, current);
        private static void OnRightHandOverlapChanged(VRPlayerInputSet set, IAbstractDynamicRigidBody? prev, IAbstractDynamicRigidBody? current)
            => ChangeHighlight(prev, current);

        private static void InitMovement(CharacterMovement3DComponent movementComp)
        {
            movementComp.StandingHeight = 1.89f;
            //movementComp.InitialPosition = new Vector3(0.0f, 10.0f, 0.0f);
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

            if (Toggles.SceneOnlyVRPawn)
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
            listener.DistanceModel = EDistanceModel.InverseDistance;
            listener.DopplerFactor = 0.5f;
            listener.SpeedOfSound = 343.3f;

            hmdTfm = vrHeadsetNode.SetTransform<VRHeadsetTransform>()!;
            hmdComp = vrHeadsetNode.AddComponent<VRHeadsetComponent>()!;

            if (!Toggles.AllowEditingInVR)
                AddVRFirstPersonDesktopView(ref pawn, vrHeadsetNode);
            
            return vrHeadsetNode;
        }

        private static void AddVRFirstPersonDesktopView(ref PawnComponent? pawn, SceneNode parentNode)
        {
            SceneNode firstPersonViewNode = new(parentNode) { Name = "FirstPersonViewNode" };
            firstPersonViewNode.SetTransform<Transform>();
            var firstPersonCam = firstPersonViewNode.AddComponent<CameraComponent>()!;
            var persp = firstPersonCam.Camera.Parameters as XRPerspectiveCameraParameters;
            persp!.HorizontalFieldOfView = FirstPersonDesktopHorizontalFieldOfView;
            persp.NearZ = 0.1f;
            persp.FarZ = 100000.0f;
            firstPersonCam.Camera.RenderPipeline = Engine.Rendering.NewRenderPipeline(stereo: false);
            firstPersonCam.Camera.RenderPipeline.OverrideProtected = true;
            firstPersonCam.CullWithFrustum = true;
            if (pawn is null)
                pawn = firstPersonCam.SetAsPlayerView(ELocalPlayerIndex.One) as PawnComponent;
            else
                pawn.CameraComponent = firstPersonCam;

            //if (setUI)
            //    canvas = CreateEditorUI(vrHeadsetNode, firstPersonCam);
        }
        #endregion

        #region Desktop

        private static DynamicRigidBodyComponent AddCameraPickupPhysicsBody(
            SceneNode cameraNode,
            Vector3 initialPosition,
            Quaternion initialRotation)
        {
            var rigidBodyTransform = cameraNode.GetTransformAs<RigidBodyTransform>(true)!;
            rigidBodyTransform.SetPositionAndRotation(initialPosition, initialRotation);

            IPhysicsGeometry.Sphere s = new(0.2f);
            PhysxMaterial mat = new(0.5f, 0.5f, 0.5f);
            PhysxShape shape = new(s, mat, PxShapeFlags.TriggerShape | PxShapeFlags.Visualization, true);
            var cameraPickup = cameraNode.AddComponent<DynamicRigidBodyComponent>()!;
            PhysxDynamicRigidBody body = new(shape, 1.0f, initialPosition, initialRotation);
            cameraPickup.RigidBody = body;

            body.Mass = 1.0f;
            body.Flags = 0;
            body.GravityEnabled = false;
            body.SimulationEnabled = true;
            body.DebugVisualize = true;

            return cameraPickup;
        }

        private static PawnComponent? CreateDesktopCamera(SceneNode cameraNode, bool isServer, bool flyable, bool addListener)
        {
            if (addListener)
            {
                var listener = cameraNode.AddComponent<AudioListenerComponent>("Desktop Flying Listener")!;
                listener.Gain = 1.0f;
                listener.DistanceModel = EDistanceModel.InverseDistance;
                listener.DopplerFactor = 0.5f;
                listener.SpeedOfSound = 343.3f;
            }

            if (!(Toggles.VRPawn && Toggles.AllowEditingInVR) && Toggles.Microphone && !(Toggles.HasAnimatedModelsToImport && Toggles.AttachMicToAnimatedModel))
                Audio.AttachMicTo(cameraNode, out _, out _, out _);

            PawnComponent pawnComp;
            if (flyable)
            {
                var editorPawn = cameraNode.AddComponent<EditorFlyingCameraPawnComponent>()!;
                if (RuntimeBootstrapState.Settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.EditorCameraRenderOnDemand)))
                    editorPawn.RenderOnDemand = RuntimeBootstrapState.Settings.EditorCameraRenderOnDemand;
                pawnComp = editorPawn;
                pawnComp!.Name = "Desktop Camera Pawn (Flyable)";
                if (cameraNode.GetComponent<CameraComponent>() is { } cameraComponent)
                    pawnComp.CameraComponent = cameraComponent;
            }
            else
            {
                pawnComp = cameraNode.AddComponent<PawnComponent>()!;
                pawnComp!.Name = "Desktop Camera Pawn";
                if (cameraNode.GetComponent<CameraComponent>() is { } cameraComponent)
                    pawnComp.CameraComponent = cameraComponent;
            }

            if (cameraNode.Parent is { } parent)
                ConfigureEditorViewCamera(parent, cameraNode);

            if (ProfileCameraMotionComponent.IsRequested())
                cameraNode.AddComponent<ProfileCameraMotionComponent>("Automated Profile Camera Motion");

            pawnComp.EnqueuePossessionByLocalPlayer(ELocalPlayerIndex.One);
            Engine.State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One).OnPawnCameraChanged();
            return pawnComp;
        }

        private static SceneNode CreateDesktopCharacterPawn(SceneNode rootNode, bool setUI)
        {
            SceneNode characterNode = new(rootNode, "Player");
            var characterTfm = characterNode.SetTransform<RigidBodyTransform>();
            characterTfm.InterpolationMode = EInterpolationMode.Interpolate;

            var movementComp = characterNode.AddComponent<CharacterMovement3DComponent>()!;
            InitMovement(movementComp);

            float spawnY = Toggles.CharacterControllerCapsuleTranslationY ?? (movementComp.HalfHeight + 0.01f);
            characterTfm.SetPositionAndRotation(new Vector3(0.0f, spawnY, 0.0f), Quaternion.Identity);

            var footNode = characterNode.NewChild("Foot Position Node");
            var footTfm = footNode.SetTransform<Transform>();
            footTfm.Translation = new Vector3(0.0f, -movementComp.HalfHeight, 0.0f);
            //footTfm.Scale = new Vector3(movementComp.StandingHeight);
            footTfm.SaveBindState();

            //create node to translate camera up half the height of the character
            SceneNode cameraOffsetNode = new(footNode, "Camera Offset");
            var cameraOffsetTfm = cameraOffsetNode.SetTransform<Transform>();
            cameraOffsetTfm.Translation = new Vector3(0.0f, (movementComp.HalfHeight * 1.8f), 0.0f);

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
            listener.DistanceModel = EDistanceModel.InverseDistance;
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

        private static SceneNode CreateCamera(
            SceneNode parentNode,
            out CameraComponent? camComp,
            float? smoothed = 50.0f,
            bool localSmoothing = true,
            string cameraName = EditorViewCameraName)
        {
            var cameraNode = new SceneNode(parentNode, cameraName);
            cameraNode.SuppressTransformDebugLineAndPoint = true;
            cameraNode.SuppressTransformTools = true;

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

            if (cameraNode.TryAddComponent(out camComp, cameraName))
            {
                camComp!.SetPerspective(60.0f, 0.1f, 100000.0f, null);
                if (Toggles.CameraAntiAliasingModeOverride.HasValue)
                    camComp.AntiAliasingModeOverride = Toggles.CameraAntiAliasingModeOverride.Value;
                ConfigureCameraPostProcessing(camComp);
            }
            else
                camComp = null;

            return cameraNode;
        }

        public static void ConfigureEditorViewCamera(SceneNode parent, SceneNode cameraNode)
        {
            cameraNode.Name = EditorViewCameraName;
            cameraNode.IsEditorOnly = true;
            cameraNode.CanDeactivate = false;
            cameraNode.SuppressTransformDebugLineAndPoint = true;
            cameraNode.SuppressTransformTools = true;

            if (parent.World is XRWorldInstance world)
            {
                world.AddToEditorScene(cameraNode);
                return;
            }

            if (parent.World is not null)
                return;

            void OnParentWorldAssigned(object? _, IXRPropertyChangedEventArgs e)
            {
                if (e.PropertyName != nameof(SceneNode.World))
                    return;

                if (parent.World is not XRWorldInstance assignedWorld)
                    return;

                assignedWorld.AddToEditorScene(cameraNode);
                parent.PropertyChanged -= OnParentWorldAssigned;
            }

            parent.PropertyChanged += OnParentWorldAssigned;
        }

        private static void ConfigureCameraPostProcessing(CameraComponent cameraComponent)
        {
            Debug.Rendering($"[UnitTestingWorld] ConfigureCameraPostProcessing Atmosphere={Toggles.InitializeAtmosphericScattering} VolumetricFog={Toggles.InitializeVolumetricFog}");
            if (!Toggles.InitializeVolumetricFog && !Toggles.InitializeAtmosphericScattering)
                return;

            var camera = cameraComponent.Camera;
            if (!camera.RenderPipeline.OverrideProtected)
                camera.RenderPipeline.OverrideProtected = true;

            if (Toggles.InitializeAtmosphericScattering)
            {
                var atmosphereStage = camera.GetPostProcessStageState<AtmosphericScatteringSettings>();
                if (atmosphereStage is null)
                    Debug.Rendering("[Atmosphere] Could not find AtmosphericScatteringSettings post-process stage on camera.");
                else if (atmosphereStage.TryGetBacking(out AtmosphericScatteringSettings? atmosphereSettings) != true || atmosphereSettings is null)
                    Debug.Rendering("[Atmosphere] AtmosphericScatteringSettings stage found but backing instance is null.");
                else
                {
                    var init = Toggles.AtmosphericScattering;
                    atmosphereSettings.Enabled = true;
                    atmosphereSettings.RenderSky = true;
                    atmosphereSettings.AerialPerspective = true;
                    atmosphereSettings.MaxDistance = init.MaxDistance;
                    atmosphereSettings.ViewSamples = init.ViewSamples;
                    atmosphereSettings.OpticalDepthSamples = init.OpticalDepthSamples;
                    atmosphereSettings.JitterStrength = init.JitterStrength;
                    atmosphereSettings.TemporalEnabled = init.TemporalEnabled;
                    Debug.Rendering($"[Atmosphere] Camera post-process configured: Enabled=true, MaxDistance={atmosphereSettings.MaxDistance}, ViewSamples={atmosphereSettings.ViewSamples}");
                }
            }

            if (Toggles.InitializeVolumetricFog)
            {
                var stage = camera.GetPostProcessStageState<VolumetricFogSettings>();
                if (stage is null)
                {
                    Debug.Rendering("[VolumetricFog] Could not find VolumetricFogSettings post-process stage on camera.");
                    return;
                }

                if (stage.TryGetBacking(out VolumetricFogSettings? settings) != true || settings is null)
                {
                    Debug.Rendering("[VolumetricFog] VolumetricFogSettings stage found but backing instance is null.");
                    return;
                }

                settings.Enabled = true;
                settings.Intensity = Toggles.VolumetricFog.Intensity;
                settings.MaxDistance = Toggles.VolumetricFog.MaxDistance;
                settings.StepSize = Toggles.VolumetricFog.StepSize;
                settings.JitterStrength = Toggles.VolumetricFog.JitterStrength;
                Debug.Rendering($"[VolumetricFog] Camera post-process configured: Enabled=true, Intensity={settings.Intensity}, MaxDistance={settings.MaxDistance}, StepSize={settings.StepSize}");
            }
        }

        public static void InitializeLocomotion(
            SceneNode rootNode,
            HumanoidComponent humanComp,
            HeightScaleBaseComponent heightScale,
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
                player.HeightScaleComponent = heightScale as VRHeightScaleComponent;
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

                if (Toggles.SceneOnlyVRPawn)
                {
                    var playspaceNode = footNode.FirstChild!;
                    var trackerColl = playspaceNode.LastChild!.GetComponent<VRTrackerCollectionComponent>()!;

                    var extOpt = rigidBodyNode.AddComponent<ExternalOptionalInputSetComponent>()!;
                    extOpt.OnRegisterInput += RegisterEmulatorActions;

                    //Crazy band-aid to register these
                    if (Toggles.AllowEditingInVR)
                        (Engine.State.MainPlayer?.ControlledPawnComponent as PawnComponent)?.OptionalInputSets.Add(extOpt);

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
