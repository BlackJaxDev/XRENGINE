using MagicPhysX;
using System.Numerics;
using XREngine.Audio;
using XREngine.Components;
using XREngine.Components.Movement;
using XREngine.Components.Physics;
using XREngine.Components.Scene.Transforms;
using XREngine.Components.VR;
using XREngine.Data.Components.Scene;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Rendering.Physics.Physx;
using XREngine.Runtime.Bootstrap;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using static XREngine.Scene.Transforms.RigidBodyTransform;

namespace XREngine.Runtime.Bootstrap.Builders;

public static class BootstrapPawnFactory
{
    public static SceneNode? CreatePlayerPawn(bool setUI, bool isServer, SceneNode rootNode)
    {
        var settings = RuntimeBootstrapState.Settings;

        SceneNode? characterPawnModelParentNode = null;
        if (settings.VRPawn)
        {
            if (settings.Locomotion)
            {
                characterPawnModelParentNode = CreateCharacterVRPawn(rootNode, out var pawn, out _, out _, out _);
                if (settings.AllowEditingInVR || settings.AddCameraVRPickup)
                {
                    SceneNode cameraNode = CreateCamera(rootNode, out var camComp, null);
                    var desktopPawn = CreateDesktopCamera(cameraNode, isServer, settings.AllowEditingInVR && !settings.AddCameraVRPickup, settings.AddCameraVRPickup, false);
                    if (setUI)
                        BootstrapEditorBridge.Current?.CreateEditorUi(rootNode, camComp, desktopPawn);
                }
                else if (setUI)
                    BootstrapEditorBridge.Current?.CreateEditorUi(characterPawnModelParentNode, null, pawn);
            }
            else
                CreateFlyingVRPawn(rootNode);
        }
        else if (settings.Locomotion)
            characterPawnModelParentNode = CreateDesktopCharacterPawn(rootNode, setUI);
        else
        {
            SceneNode cameraNode = CreateCamera(rootNode, out var camComp, null);
            var pawn = CreateDesktopCamera(cameraNode, isServer, true, false, true);
            if (setUI)
                BootstrapEditorBridge.Current?.CreateEditorUi(rootNode, camComp, pawn);
        }

        return characterPawnModelParentNode;
    }

    private static SceneNode CreateCharacterVRPawn(
        SceneNode rootNode,
        out CharacterPawnComponent pawn,
        out VRHeadsetTransform hmdTfm,
        out VRControllerTransform leftTfm,
        out VRControllerTransform rightTfm)
    {
        var settings = RuntimeBootstrapState.Settings;

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

        float spawnY = settings.CharacterControllerCapsuleTranslationY ?? (movementComp.HalfHeight + 0.01f);
        characterTfm.SetPositionAndRotation(new Vector3(0.0f, spawnY, 0.0f), Quaternion.Identity);

        if (!settings.AllowEditingInVR)
            characterComp.EnqueuePossessionByLocalPlayer(ELocalPlayerIndex.One);

        SceneNode localRotationNode = vrPlayspaceNode.NewChild("LocalRotationNode");
        characterComp.IgnoreViewTransformPitch = true;
        characterComp.ViewRotationTransform = localRotationNode.GetTransformAs<Transform>(true)!;

        var footNode = localRotationNode.NewChild("Foot Position Node");
        var footTfm = footNode.SetTransform<Transform>();
        footTfm.Translation = new Vector3(0.0f, -movementComp.HalfHeight, 0.0f);
        footTfm.SaveBindState();

        var playspaceNode = footNode.NewChild("Playspace Node");

        CreateVRDevices(out hmdTfm, out leftTfm, out rightTfm, vrPlayspaceNode, characterComp, vrInput, playspaceNode);

        return footNode;
    }

    private static void CreateVRDevices(
        out VRHeadsetTransform hmdTfm,
        out VRControllerTransform leftTfm,
        out VRControllerTransform rightTfm,
        SceneNode vrPlayspaceNode,
        CharacterPawnComponent characterComp,
        VRPlayerInputSet vrInput,
        SceneNode playspaceNode)
    {
        PawnComponent? refPawn = characterComp;
        characterComp.InputOrientationTransform = AddHeadsetNode(out hmdTfm, out _, playspaceNode, ref refPawn).Transform;

        AddHandControllerNode(out leftTfm, playspaceNode, true);
        AddHandControllerNode(out rightTfm, playspaceNode, false);
        _ = AddTrackerCollectionNode(playspaceNode);

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
        movementComp.Velocity = new Vector3(0.0f, 0.0f, 0.0f);
        movementComp.InputLerpSpeed = 0.9f;
    }

    private static void AddHandControllerNode(out VRControllerTransform controllerTfm, SceneNode parentNode, bool left)
    {
        var settings = RuntimeBootstrapState.Settings;
        SceneNode controllerNode = parentNode.NewChild($"VR{(left ? "Left" : "Right")}ControllerNode");

        controllerTfm = controllerNode.SetTransform<VRControllerTransform>();
        controllerTfm.LeftHand = left;

        if (settings.EmulatedVRPawn)
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

    private static void CreateFlyingVRPawn(SceneNode rootNode)
    {
        SceneNode vrPlayspaceNode = new(rootNode) { Name = "VRPlayspaceNode" };
        _ = vrPlayspaceNode.SetTransform<Transform>();
        PawnComponent? pawn = null;
        AddHeadsetNode(out _, out _, vrPlayspaceNode, ref pawn);
        AddHandControllerNode(out _, vrPlayspaceNode, true);
        AddHandControllerNode(out _, vrPlayspaceNode, false);
        _ = AddTrackerCollectionNode(vrPlayspaceNode);
        _ = pawn?.SceneNode?.AddComponent<VRPlayerInputSet>();
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
        ref PawnComponent? pawn)
    {
        var settings = RuntimeBootstrapState.Settings;

        SceneNode vrHeadsetNode = parentNode.NewChild("VRHeadsetNode");
        var listener = vrHeadsetNode.AddComponent<AudioListenerComponent>("VR HMD Listener")!;
        listener.Gain = 1.0f;
        listener.DistanceModel = EDistanceModel.InverseDistance;
        listener.DopplerFactor = 0.5f;
        listener.SpeedOfSound = 343.3f;

        hmdTfm = vrHeadsetNode.SetTransform<VRHeadsetTransform>()!;
        hmdComp = vrHeadsetNode.AddComponent<VRHeadsetComponent>()!;

        if (!settings.AllowEditingInVR)
            AddVRFirstPersonDesktopView(ref pawn, vrHeadsetNode);

        return vrHeadsetNode;
    }

    private static void AddVRFirstPersonDesktopView(ref PawnComponent? pawn, SceneNode parentNode)
    {
        SceneNode firstPersonViewNode = new(parentNode) { Name = "FirstPersonViewNode" };
        var firstPersonViewTfm = firstPersonViewNode.SetTransform<SmoothedParentConstraintTransform>();
        firstPersonViewTfm.TranslationInterpolationSpeed = null;
        firstPersonViewTfm.ScaleInterpolationSpeed = null;
        firstPersonViewTfm.QuaternionInterpolationSpeed = null;
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
    }

    private static PawnComponent? CreateDesktopCamera(SceneNode cameraNode, bool isServer, bool flyable, bool addPhysicsBody, bool addListener)
    {
        var settings = RuntimeBootstrapState.Settings;

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
            listener.DistanceModel = EDistanceModel.InverseDistance;
            listener.DopplerFactor = 0.5f;
            listener.SpeedOfSound = 343.3f;
        }

        if (!(settings.VRPawn && settings.AllowEditingInVR) && settings.Microphone && !(settings.HasAnimatedModelsToImport && settings.AttachMicToAnimatedModel))
        {
        }

        PawnComponent pawnComp;
        if (flyable)
        {
            pawnComp = BootstrapFlyableCameraFactory.CreateFlyableCameraPawn(cameraNode, !isServer) as PawnComponent
                ?? throw new InvalidOperationException("Bootstrap flyable camera factory did not return a PawnComponent.");
            pawnComp.Name = "Desktop Camera Pawn (Flyable)";
        }
        else
        {
            pawnComp = cameraNode.AddComponent<PawnComponent>()!;
            pawnComp.Name = "Desktop Camera Pawn";
        }

        pawnComp.EnqueuePossessionByLocalPlayer(ELocalPlayerIndex.One);
        return pawnComp;
    }

    private static SceneNode CreateDesktopCharacterPawn(SceneNode rootNode, bool setUI)
    {
        var settings = RuntimeBootstrapState.Settings;

        SceneNode characterNode = new(rootNode, "Player");
        var characterTfm = characterNode.SetTransform<RigidBodyTransform>();
        characterTfm.InterpolationMode = EInterpolationMode.Interpolate;

        var movementComp = characterNode.AddComponent<CharacterMovement3DComponent>()!;
        InitMovement(movementComp);

        float spawnY = settings.CharacterControllerCapsuleTranslationY ?? (movementComp.HalfHeight + 0.01f);
        characterTfm.SetPositionAndRotation(new Vector3(0.0f, spawnY, 0.0f), Quaternion.Identity);

        var footNode = characterNode.NewChild("Foot Position Node");
        var footTfm = footNode.SetTransform<Transform>();
        footTfm.Translation = new Vector3(0.0f, -movementComp.HalfHeight, 0.0f);
        footTfm.SaveBindState();

        SceneNode cameraOffsetNode = new(footNode, "Camera Offset");
        var cameraOffsetTfm = cameraOffsetNode.SetTransform<Transform>();
        cameraOffsetTfm.Translation = new Vector3(0.0f, movementComp.HalfHeight * 1.8f, 0.0f);

        SceneNode cameraParentNode;
        if (settings.ThirdPersonPawn)
        {
            cameraParentNode = cameraOffsetNode.NewChildWithTransform<BoomTransform>(out var boomTfm, "3rd Person Camera Boom");
            boomTfm.MaxLength = 10.0f;
            boomTfm.ZoomOutSpeed = 5.0f;
        }
        else
            cameraParentNode = cameraOffsetNode;

        SceneNode cameraNode = CreateCamera(cameraParentNode, out CameraComponent? camComp, null, !settings.ThirdPersonPawn);

        var listener = cameraNode.AddComponent<AudioListenerComponent>("Desktop Character Listener")!;
        listener.Gain = 1.0f;
        listener.DistanceModel = EDistanceModel.InverseDistance;
        listener.DopplerFactor = 0.5f;
        listener.SpeedOfSound = 343.3f;

        var characterComp = characterNode.AddComponent<CharacterPawnComponent>("TestPawn")!;
        characterComp.CameraComponent = camComp;
        characterComp.InputOrientationTransform = cameraNode.Transform;
        characterComp.ViewRotationTransform = cameraOffsetTfm;

        if (setUI && camComp is not null)
            BootstrapEditorBridge.Current?.CreateEditorUi(rootNode, camComp, characterComp);

        characterComp.EnqueuePossessionByLocalPlayer(ELocalPlayerIndex.One);

        return footNode;
    }

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
        {
            camComp!.SetPerspective(60.0f, 0.1f, 100000.0f, null);
            ConfigureCameraPostProcessing(camComp);
        }
        else
            camComp = null;

        return cameraNode;
    }

    private static void ConfigureCameraPostProcessing(CameraComponent cameraComponent)
    {
        var settings = RuntimeBootstrapState.Settings;
        Debug.Out($"[VolumetricFog] BootstrapPawnFactory.ConfigureCameraPostProcessing InitializeVolumetricFog = {settings.InitializeVolumetricFog}");
        if (!settings.InitializeVolumetricFog)
            return;

        var camera = cameraComponent.Camera;
        if (!camera.RenderPipeline.OverrideProtected)
            camera.RenderPipeline.OverrideProtected = true;

        var stage = camera.GetPostProcessStageState<VolumetricFogSettings>();
        if (stage is null)
        {
            Debug.Out("[VolumetricFog] Runtime camera is missing VolumetricFogSettings post-process stage.");
            return;
        }

        if (stage.TryGetBacking(out VolumetricFogSettings? fogSettings) != true || fogSettings is null)
        {
            Debug.Out("[VolumetricFog] Runtime camera found VolumetricFogSettings stage but backing instance is null.");
            return;
        }

        fogSettings.Enabled = true;
        fogSettings.Intensity = settings.VolumetricFog.Intensity;
        fogSettings.MaxDistance = settings.VolumetricFog.MaxDistance;
        fogSettings.StepSize = settings.VolumetricFog.StepSize;
        fogSettings.JitterStrength = settings.VolumetricFog.JitterStrength;

        Debug.Out($"[VolumetricFog] Runtime camera configured: Enabled=true, Intensity={fogSettings.Intensity}, MaxDistance={fogSettings.MaxDistance}, StepSize={fogSettings.StepSize}, JitterStrength={fogSettings.JitterStrength}");
    }
}