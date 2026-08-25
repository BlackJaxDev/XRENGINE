using System.Numerics;
using MonkeyBallVR;
using NUnit.Framework;
using OpenVrAction = OpenVR.NET.Input.Action;
using Shouldly;
using XREngine.Components;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Games;

[TestFixture]
public sealed class MonkeyBallInputIntegrationTests
{
    [Test]
    public void CookedPawn_PossessionRoutesKeyboardGamepadVrAndLateActionRefresh()
    {
        using IDisposable adapterLease = RuntimeAdapterBootstrap.InstallEngineHostServices(RuntimeAdapterProfile.Input);
        IRuntimeVrInputServices installedVrServices = RuntimeVrInputServices.Current;
        RecordingVrInputServices vrServices = new();
        LocalPlayerController? controller = null;

        try
        {
            RuntimeVrInputServices.Current = vrServices;
            MonkeyBallPawnComponent pawn = CreateCookedPawn();
            RecordingGameInputTarget target = new();
            pawn.Bind(target);

            controller = new LocalPlayerController(ELocalPlayerIndex.One);
            WindowSnapshotKeyboard keyboard = new(0);
            WindowSnapshotGamepad gamepad = new(0);
            controller.Input.UpdateDevices(keyboard, null, gamepad, vrServices.Actions);
            ((IPawnController)controller).ControlledPawnComponent = pawn;
            controller.Input.UpdateDevices(keyboard, null, gamepad, vrServices.Actions);

            pawn.Controller.ShouldBeSameAs(controller);
            ((IPawnController)controller).ControlledPawnComponent.ShouldBeSameAs(pawn);
            vrServices.HasVector2Registration("Global", "Tilt").ShouldBeTrue();
            vrServices.HasBoolRegistration("Global", "Reset").ShouldBeTrue();
            vrServices.HasBoolRegistration("Global", "Pause").ShouldBeTrue();

            WindowInputSnapshotAccumulator keyboardFrames = new();

            ApplyKey(EKey.W, true);
            target.Tilt.ShouldBe(new Vector2(0.0f, 1.0f));
            ApplyKey(EKey.W, false);
            ApplyKey(EKey.Up, true);
            target.Tilt.ShouldBe(new Vector2(0.0f, 1.0f));
            ApplyKey(EKey.Up, false);

            ApplyKey(EKey.S, true);
            target.Tilt.ShouldBe(new Vector2(0.0f, -1.0f));
            ApplyKey(EKey.S, false);
            ApplyKey(EKey.Down, true);
            target.Tilt.ShouldBe(new Vector2(0.0f, -1.0f));
            ApplyKey(EKey.Down, false);

            ApplyKey(EKey.A, true);
            target.Tilt.ShouldBe(new Vector2(-1.0f, 0.0f));
            ApplyKey(EKey.A, false);
            ApplyKey(EKey.Left, true);
            target.Tilt.ShouldBe(new Vector2(-1.0f, 0.0f));
            ApplyKey(EKey.Left, false);

            ApplyKey(EKey.D, true);
            target.Tilt.ShouldBe(new Vector2(1.0f, 0.0f));
            ApplyKey(EKey.D, false);
            ApplyKey(EKey.Right, true);
            target.Tilt.ShouldBe(new Vector2(1.0f, 0.0f));
            ApplyKey(EKey.Right, false);

            ApplyKey(EKey.W, true);
            ApplyKey(EKey.S, true);
            target.Tilt.ShouldBe(Vector2.Zero);
            ApplyKey(EKey.W, false);
            ApplyKey(EKey.S, false);
            ApplyKey(EKey.A, true);
            ApplyKey(EKey.D, true);
            target.Tilt.ShouldBe(Vector2.Zero);
            ApplyKey(EKey.A, false);
            ApplyKey(EKey.D, false);

            gamepad.ApplySnapshot(keyboardFrames.Publish(
                keyboardCount: 1,
                mouseCount: 0,
                gamepadCount: 1,
                isFocused: true,
                isMouseCaptured: false,
                primaryGamepad: new WindowGamepadSnapshot(
                    IsConnected: true,
                    PressedButtonMask: 0,
                    LeftTrigger: 0.0f,
                    RightTrigger: 0.0f,
                    LeftThumbstickX: 0.5f,
                    LeftThumbstickY: 0.75f,
                    RightThumbstickX: 0.0f,
                    RightThumbstickY: 0.0f)));
            controller.Input.TickStates(1.0f / 60.0f);
            target.Tilt.X.ShouldBe(0.5f);
            target.Tilt.Y.ShouldBe(0.75f);

            gamepad.ApplySnapshot(keyboardFrames.Publish(
                keyboardCount: 1,
                mouseCount: 0,
                gamepadCount: 1,
                isFocused: true,
                isMouseCaptured: false,
                primaryGamepad: new WindowGamepadSnapshot(
                    IsConnected: true,
                    PressedButtonMask: 0,
                    LeftTrigger: 0.0f,
                    RightTrigger: 0.0f,
                    LeftThumbstickX: 0.0f,
                    LeftThumbstickY: 0.0f,
                    RightThumbstickX: 0.0f,
                    RightThumbstickY: 0.0f)));
            controller.Input.TickStates(1.0f / 60.0f);

            vrServices.PublishVector2("Global", "Tilt", new Vector2(0.2f, -0.9f));
            target.Tilt.X.ShouldBe(0.2f);
            target.Tilt.Y.ShouldBe(-0.9f);
            vrServices.PublishVector2("Global", "Tilt", Vector2.Zero);

            ushort faceDown = (ushort)(1 << (int)EGamePadButton.FaceDown);
            gamepad.ApplySnapshot(keyboardFrames.Publish(
                keyboardCount: 1,
                mouseCount: 0,
                gamepadCount: 1,
                isFocused: true,
                isMouseCaptured: false,
                primaryGamepad: new WindowGamepadSnapshot(
                    IsConnected: true,
                    PressedButtonMask: faceDown,
                    LeftTrigger: 0.0f,
                    RightTrigger: 0.0f,
                    LeftThumbstickX: 0.0f,
                    LeftThumbstickY: 0.0f,
                    RightThumbstickX: 0.0f,
                    RightThumbstickY: 0.0f)));
            controller.Input.TickStates(1.0f / 60.0f);
            target.ResetCalls.ShouldBe(1);

            vrServices.PublishBool("Global", "Reset", true);
            target.ResetCalls.ShouldBe(2);

            int registrationsBeforeRefresh = vrServices.Vector2RegistrationCount;
            vrServices.RaiseActionsChanged();
            vrServices.Vector2RegistrationCount.ShouldBeGreaterThan(registrationsBeforeRefresh);

            void ApplyKey(EKey key, bool pressed)
            {
                if (pressed)
                    keyboardFrames.RecordKeyDown(key);
                else
                    keyboardFrames.RecordKeyUp(key);

                keyboard.ApplySnapshot(keyboardFrames.Publish(
                    keyboardCount: 1,
                    mouseCount: 0,
                    gamepadCount: 1,
                    isFocused: true,
                    isMouseCaptured: false));
                controller.Input.TickStates(1.0f / 60.0f);
            }
        }
        finally
        {
            if (controller is not null)
            {
                ((IPawnController)controller).ControlledPawnComponent = null;
                controller.Destroy(now: true);
            }

            RuntimeVrInputServices.Current = installedVrServices;
        }
    }

    private static MonkeyBallPawnComponent CreateCookedPawn()
    {
        SceneNode root = new("Cooked MonkeyBall Input Root", new Transform());
        root.AddComponent<MonkeyBallPawnComponent>().ShouldNotBeNull();
        MonkeyBallWorldAsset authored = new(
            "Cooked MonkeyBall Input World",
            new XRScene("Cooked MonkeyBall Input Scene", root));

        byte[] payload = MonkeyBallWorldCookedSerializer.Serialize(authored);
        MonkeyBallWorldAsset cooked = MonkeyBallWorldCookedSerializer.Deserialize(payload);
        cooked.Scenes.Count.ShouldBe(1);
        cooked.Scenes[0].RootNodes.Count.ShouldBe(1);
        return cooked.Scenes[0].RootNodes[0]
            .GetComponent<MonkeyBallPawnComponent>()
            .ShouldNotBeNull();
    }

    private sealed class RecordingGameInputTarget : IMonkeyBallGameInputTarget
    {
        public Vector2 Tilt { get; private set; }
        public int ResetCalls { get; private set; }
        public int PauseCalls { get; private set; }

        public void SetTilt(Vector2 tilt)
            => Tilt = tilt;

        public void ResetRound()
            => ResetCalls++;

        public void TogglePause()
            => PauseCalls++;
    }

    private sealed class RecordingVrInputServices :
        IRuntimeVrInputServices,
        IRuntimeVrLegacyActionServices
    {
        private readonly Dictionary<(string Category, string Name), System.Action<bool>> _boolActions = [];
        private readonly Dictionary<(string Category, string Name), RuntimeVrVector2Changed> _vector2Actions = [];

        public RuntimeVrRuntimeKind ActiveRuntime => RuntimeVrRuntimeKind.OpenXR;
        public string ActiveServiceName => "MonkeyBall Test OpenXR";
        public int Vector2RegistrationCount { get; private set; }
        public Dictionary<string, Dictionary<string, OpenVrAction>> Actions { get; } = [];
        public event System.Action<Dictionary<string, Dictionary<string, OpenVrAction>>>? ActionsChanged;

        public bool HasBoolRegistration(string category, string name)
            => _boolActions.ContainsKey((category, name));

        public bool HasVector2Registration(string category, string name)
            => _vector2Actions.ContainsKey((category, name));

        public void PublishBool(string category, string name, bool value)
            => _boolActions[(category, name)](value);

        public void PublishVector2(string category, string name, Vector2 value)
            => _vector2Actions[(category, name)](Vector2.Zero, value);

        public void RaiseActionsChanged()
            => ActionsChanged?.Invoke(Actions);

        public void Update(float delta)
        {
        }

        public bool RegisterBoolAction(string category, string name, System.Action<bool> callback, bool unregister)
        {
            if (unregister)
                return _boolActions.Remove((category, name));

            _boolActions[(category, name)] = callback;
            return true;
        }

        public bool RegisterVector2Action(string category, string name, RuntimeVrVector2Changed callback, bool unregister)
        {
            if (unregister)
                return _vector2Actions.Remove((category, name));

            _vector2Actions[(category, name)] = callback;
            Vector2RegistrationCount++;
            return true;
        }

        public bool RegisterFloatAction(string category, string name, RuntimeVrScalarChanged callback, bool unregister) => true;
        public bool RegisterVector3Action(string category, string name, RuntimeVrVector3Changed callback, bool unregister) => true;
        public bool RegisterPoseAction(string category, string name, RuntimeVrPoseKind poseKind, bool leftHand, RuntimeVrPoseChanged callback, bool unregister) => true;
        public bool RegisterHandSkeletonSummaryAction(string category, string name, bool leftHand, RuntimeVrSkeletonSummaryChanged callback, bool unregister) => true;
        public bool RegisterHandSkeletonQuery(string category, string name, bool leftHand, bool unregister) => true;
        public bool TryGetPose(bool leftHand, RuntimeVrPoseKind poseKind, RuntimeVrPoseTiming timing, out RuntimeVrPoseState pose) { pose = default; return false; }
        public bool TryGetHandJoint(bool leftHand, RuntimeVrHandJoint joint, out RuntimeVrHandJointState state) { state = default; return false; }
        public bool TryGetSkeletonSummary(bool leftHand, out RuntimeVrSkeletonSummary summary) { summary = default; return false; }
        public bool VibrateAction(string category, string name, double duration, double frequency = 40, double amplitude = 1, double delay = 0) => true;
        public bool StopVibration(string category, string name) => true;
    }
}
