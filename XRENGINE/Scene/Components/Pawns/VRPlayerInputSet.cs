using Assimp;
using MagicPhysX;
using System.Numerics;
using XREngine.Core;
using XREngine.Core.Attributes;
using XREngine.Components.Physics;
using XREngine.Data.Components.Scene;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Physics.Physx;
using XREngine.Rendering.Physics.Physx.Joints;
using XREngine.Scene;
using XREngine.Scene.Physics.Joints;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    /// <summary>
    /// Add-on for the CharacterPawnComponent that adds VR-specific input handling.
    /// Normal keyboard, mouse and gamepad input is available both on desktop and in VR to allow for special VR control setups.
    /// </summary>
    [RequireComponents(typeof(CharacterPawnComponent))]
    public class VRPlayerInputSet : OptionalInputSetComponent, IRenderable
    {
        public CharacterPawnComponent CharacterPawn => GetSiblingComponent<CharacterPawnComponent>(true)!;

        private VRControllerTransform? _rightHandTransform;
        public VRControllerTransform? RightHandTransform
        {
            get => _rightHandTransform;
            set => SetField(ref _rightHandTransform, value);
        }

        private VRControllerTransform? _leftHandTransform;
        public VRControllerTransform? LeftHandTransform
        {
            get => _leftHandTransform;
            set => SetField(ref _leftHandTransform, value);
        }

        private float _grabRadius = 0.1f;
        public float GrabRadius
        {
            get => _grabRadius;
            set => SetField(ref _grabRadius, value);
        }

        private float _grabForceThreshold = 0.2f;
        public float GrabForceThreshold
        {
            get => _grabForceThreshold;
            set => SetField(ref _grabForceThreshold, value);
        }

        private float _releaseForceThreshold = 0.1f;
        public float ReleaseForceThreshold
        {
            get => _releaseForceThreshold;
            set => SetField(ref _releaseForceThreshold, value);
        }

        private IAbstractDynamicRigidBody? _leftHandOverlap;
        [RuntimeOnly]
        public IAbstractDynamicRigidBody? LeftHandOverlap
        {
            get => _leftHandOverlap;
            set => SetField(ref _leftHandOverlap, value);
        }

        private IAbstractDynamicRigidBody? _rightHandOverlap;
        [RuntimeOnly]
        public IAbstractDynamicRigidBody? RightHandOverlap
        {
            get => _rightHandOverlap;
            set => SetField(ref _rightHandOverlap, value);
        }

        private IAbstractDynamicRigidBody? _rightHandBody;
        [RuntimeOnly]
        public IAbstractDynamicRigidBody? RightHandRigidBody
        {
            get => _rightHandBody;
            set => SetField(ref _rightHandBody, value);
        }

        private IAbstractDynamicRigidBody? _leftHandBody;
        [RuntimeOnly]
        public IAbstractDynamicRigidBody? LeftHandRigidBody
        {
            get => _leftHandBody;
            set => SetField(ref _leftHandBody, value);
        }

        private readonly RuntimeDistanceConstraintOwner _leftHandConstraintOwner = new();
        [RuntimeOnly]
        public IAbstractDistanceJoint? LeftHandConstraint => _leftHandConstraintOwner.Constraint;

        private readonly RuntimeDistanceConstraintOwner _rightHandConstraintOwner = new();
        [RuntimeOnly]
        public IAbstractDistanceJoint? RightHandConstraint => _rightHandConstraintOwner.Constraint;

        private float _damping = 0.5f;
        public float Damping
        {
            get => _damping;
            set => SetField(ref _damping, value);
        }

        private float _stiffness = 0.5f;
        public float Stiffness
        {
            get => _stiffness;
            set => SetField(ref _stiffness, value);
        }

        private float? _minDistance = 0;
        public float? MinDistance
        {
            get => _minDistance;
            set => SetField(ref _minDistance, value);
        }

        private float? _maxDistance = 0;
        public float? MaxDistance
        {
            get => _maxDistance;
            set => SetField(ref _maxDistance, value);
        }

        private float _tolerance = 0.1f;
        public float Tolerance
        {
            get => _tolerance;
            set => SetField(ref _tolerance, value);
        }

        private float _contactDistance = 0.1f;
        public float ContactDistance
        {
            get => _contactDistance;
            set => SetField(ref _contactDistance, value);
        }

        private bool _springEnabled = true;

        public VRPlayerInputSet()
        {
            RenderedObjects = [RenderInfo3D.New(this, new RenderCommandMethod3D(EDefaultRenderPass.PostRender, PostRender))];
        }

        private bool _screenshotRequested = false;
        private void PostRender()
        {
            if (!Engine.Rendering.State.IsStereoPass || !_screenshotRequested)
                return;

            using var prof = Engine.Profiler.Start("VRPlayerInputSet.PostRender.Screenshot");

            _screenshotRequested = false;

            var pipeline = Engine.Rendering.State.CurrentRenderingPipeline;
            if (pipeline is null)
                return;

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string capturePath = Path.Combine(desktop, $"{pipeline.GetType().Name}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");

            BoundingRectangle vp = new BoundingRectangle();
            AbstractRenderer.Current?.GetScreenshotAsync(vp, false, (img, index) =>
            {
                Utility.EnsureDirPathExists(capturePath);
                img?.Flip();
                img?.Write(Path.Combine(capturePath, $"Screenshot_{index:D4}.png"));
            });
        }

        public bool SpringEnabled
        {
            get => _springEnabled;
            set => SetField(ref _springEnabled, value);
        }

        public RenderInfo[] RenderedObjects { get; }

        public delegate void DelPauseToggled(bool leftHand);
        public event DelPauseToggled? PauseToggled;

        public event Action<bool>? IsMutedChanged;

        public enum EVRActionCategory
        {
            /// <summary>
            /// Global actions are always available.
            /// </summary>
            Global,
            /// <summary>
            /// Actions that are only available when one controller is off.
            /// </summary>
            OneHanded,
            /// <summary>
            /// Actions that are enabled when the quick menu (the menu on the controller) is open.
            /// </summary>
            QuickMenu,
            /// <summary>
            /// Actions that are enabled when the main menu is fully open.
            /// </summary>
            Menu,
            /// <summary>
            /// Actions that are enabled when the avatar's menu is open.
            /// </summary>
            AvatarMenu,
        }

        public enum EVRGameAction
        {
            Interact,
            Jump,
            ToggleMute,
            GrabLeft,
            GrabRight,
            PlayspaceDragLeft,
            PlayspaceDragRight,
            ToggleQuickMenu,
            ToggleMenu,
            ToggleAvatarMenu,
            LeftHandPose,
            RightHandPose,
            Locomote,
            Turn,
        }

        public override void RegisterInput(InputInterface input)
        {
            input.RegisterVRBoolAction(EVRActionCategory.Global, EVRGameAction.Jump, CharacterPawn.Jump);
            input.RegisterVRVector2Action(EVRActionCategory.Global, EVRGameAction.Locomote, Locomote);
            input.RegisterVRVector2Action(EVRActionCategory.Global, EVRGameAction.Turn, Turn);
            input.RegisterVRFloatAction(EVRActionCategory.Global, EVRGameAction.GrabLeft, GrabLeft);
            input.RegisterVRFloatAction(EVRActionCategory.Global, EVRGameAction.GrabRight, GrabRight);
            input.RegisterVRBoolAction(EVRActionCategory.Global, EVRGameAction.ToggleQuickMenu, ToggleQuickMenu);
            input.RegisterVRBoolAction(EVRActionCategory.Global, EVRGameAction.ToggleMute, ToggleMute);
            input.RegisterKeyEvent(EKey.S, EButtonInputType.Pressed, Screenshot);
            input.RegisterKeyEvent(EKey.V, EButtonInputType.Pressed, ToggleMute);
        }

        private void Screenshot()
            => _screenshotRequested = true;

        private bool _isMuted = false;
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                SetField(ref _isMuted, value);
                IsMutedChanged?.Invoke(value);
            }
        }

        private void ToggleMute()
            => IsMuted = !IsMuted;
        private void ToggleMute(bool enabled)
            => IsMuted = enabled;

        private void ToggleQuickMenu(bool enabled)
            => PauseToggled?.Invoke(enabled);

        private void GrabLeft(float lastGrabForce, float grabForce)
        {
            if (grabForce > GrabForceThreshold)
                Grab(true);
            else if (grabForce < ReleaseForceThreshold)
                Release(true);
        }

        private void GrabRight(float lastGrabForce, float grabForce)
        {
            if (grabForce > GrabForceThreshold)
                Grab(false);
            else if (grabForce < ReleaseForceThreshold)
                Release(false);
        }

        private void Release(bool left)
        {
            RuntimeDistanceConstraintOwner owner = left
                ? _leftHandConstraintOwner
                : _rightHandConstraintOwner;
            if (!owner.Release())
                return;

            IAbstractDynamicRigidBody? item = left ? LeftHandOverlap : RightHandOverlap;
            if (item is null)
                return;

            if (left)
                LeftHandReleased?.Invoke(this, item);
            else
                RightHandReleased?.Invoke(this, item);
        }

        private void Grab(bool left)
        {
            //Attach constraint between hand and object

            //First, check if we can grab anything new
            if (left)
            {
                if (LeftHandOverlap is null)
                    return;
                if (LeftHandConstraint is not null)
                    return;
            }
            else
            {
                if (RightHandOverlap is null)
                    return;
                if (RightHandConstraint is not null)
                    return;
            }

            AbstractPhysicsScene? physicsScene = WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene;
            if (physicsScene is null)
                return;

            var handRB = left ? LeftHandRigidBody : RightHandRigidBody;
            if (handRB is null) //Can't grab without a hand
                return;
            
            var itemRB = left ? LeftHandOverlap : RightHandOverlap;
            if (itemRB is null) //Can't grab nothing
                return;

            var (handPos, handRot) = handRB.Transform;
            var localFrameHand = (Vector3.Zero, Quaternion.Identity);
            //The item's local frame is the hand transform in the item's local space
            var (itemPosition, itemRotation) = itemRB.Transform;
            Quaternion inverseItemRotation = Quaternion.Inverse(itemRotation);
            var localFrameItem = (
                Vector3.Transform(handPos - itemPosition, inverseItemRotation),
                Quaternion.Multiply(inverseItemRotation, handRot));
            RuntimeDistanceConstraintOwner owner = left
                ? _leftHandConstraintOwner
                : _rightHandConstraintOwner;
            CreateGrabConstraint(owner, physicsScene, itemRB, localFrameItem, handRB, localFrameHand);

            if (left)
                LeftHandGrabbed?.Invoke(this, itemRB);
            else
                RightHandGrabbed?.Invoke(this, itemRB);
            HandGrabbed?.Invoke(this, itemRB, left);
        }

        private void CreateGrabConstraint(
            RuntimeDistanceConstraintOwner owner,
            AbstractPhysicsScene physicsScene,
            IAbstractDynamicRigidBody item,
            (Vector3 Zero, Quaternion Identity) localFrameItem,
            IAbstractDynamicRigidBody hand,
            (Vector3 Zero, Quaternion Identity) localFrameHand)
        {
            RuntimeDistanceConstraintSettings settings = new(
                MinDistance: MinDistance ?? 0.0f,
                MaxDistance: MaxDistance ?? 0.0f,
                EnableMinDistance: MinDistance.HasValue,
                EnableMaxDistance: MaxDistance.HasValue,
                Stiffness: Stiffness,
                Damping: Damping,
                Tolerance: Tolerance);
            owner.Bind(
                physicsScene,
                item,
                new JointAnchor(localFrameItem.Zero, localFrameItem.Identity),
                hand,
                new JointAnchor(localFrameHand.Zero, localFrameHand.Identity),
                settings);
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (RightHandRigidBody is null && RightHandTransform is not null)
                InitRightHandBody();
            if (LeftHandRigidBody is null && LeftHandTransform is not null)
                InitLeftHandBody();
        }

        protected override void OnComponentDeactivated()
        {
            Release(left: true);
            Release(left: false);
            base.OnComponentDeactivated();
            if (RightHandRigidBody is not null)
                DeinitRightHandBody();
            if (LeftHandRigidBody is not null)
                DeinitLeftHandBody();
        }

        private void DeinitRightHandBody()
        {
            Release(left: false);
            var tfm = RightHandTransform;
            if (tfm is not null)
                tfm.WorldMatrixChanged -= RightHandTransform_WorldMatrixChanged;
            RightHandRigidBody?.Destroy();
            RightHandRigidBody = null;
        }

        private void DeinitLeftHandBody()
        {
            Release(left: true);
            var tfm = LeftHandTransform;
            if (tfm is not null)
                tfm.WorldMatrixChanged -= LeftHandTransform_WorldMatrixChanged;
            LeftHandRigidBody?.Destroy();
            LeftHandRigidBody = null;
        }

        private void InitLeftHandBody()
        {
            var tfm = LeftHandTransform;
            if (tfm is null)
                return;

            tfm.WorldMatrixChanged += LeftHandTransform_WorldMatrixChanged;
            if (WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene is { } physicsScene)
            {
                LeftHandRigidBody?.Destroy();
                LeftHandRigidBody = NewHandRigidBody(physicsScene, tfm);
            }
        }

        private void InitRightHandBody()
        {
            var tfm = RightHandTransform;
            if (tfm is null)
                return;

            tfm.WorldMatrixChanged += RightHandTransform_WorldMatrixChanged;
            if (WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene is { } physicsScene)
            {
                RightHandRigidBody?.Destroy();
                RightHandRigidBody = NewHandRigidBody(physicsScene, tfm);
            }
        }

        private static IAbstractDynamicRigidBody? NewHandRigidBody(
            AbstractPhysicsScene physicsScene,
            VRControllerTransform tfm)
        {
            IAbstractDynamicRigidBody? body = physicsScene.BackendService.CreateDynamicRigidBody(
                new PhysicsRigidBodyCreateInfo(
                    [],
                    null,
                    null,
                    null,
                    (tfm.WorldTranslation, tfm.WorldRotation),
                    Vector3.Zero,
                    Quaternion.Identity,
                    1.0f,
                    new LayerMask(1))
                {
                    GravityEnabled = false,
                    BodyFlags = PhysicsRigidBodyFlags.Kinematic | PhysicsRigidBodyFlags.UseKinematicTargetForQueries,
                });
            return body;
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(RightHandTransform):
                        if (RightHandTransform is not null && IsActive)
                            DeinitRightHandBody();
                        break;
                    case nameof(LeftHandTransform):
                        if (LeftHandTransform is not null && IsActive)
                            DeinitLeftHandBody();
                        break;
                    case nameof(RightHandRigidBody):
                        {
                            Release(left: false);
                            if (RightHandRigidBody is not null)
                                WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene.RemoveActor(RightHandRigidBody);
                        }
                        break;
                    case nameof(LeftHandRigidBody):
                        {
                            Release(left: true);
                            if (LeftHandRigidBody is not null)
                                WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene.RemoveActor(LeftHandRigidBody);
                        }
                        break;
                }
            }
            return change;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(RightHandTransform):
                    if (RightHandTransform is not null && IsActive)
                        InitRightHandBody();
                    break;
                case nameof(LeftHandTransform):
                    if (LeftHandTransform is not null && IsActive)
                        InitLeftHandBody();
                    break;
                case nameof(RightHandRigidBody):
                    {
                        if (RightHandRigidBody is null)
                            break;
                        WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene.AddActor(RightHandRigidBody);
                        SetHandKinematicTarget(RightHandRigidBody, RightHandTransform);
                    }
                    break;
                case nameof(LeftHandRigidBody):
                    {
                        if (LeftHandRigidBody is null)
                            break;
                        WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene.AddActor(LeftHandRigidBody);
                        SetHandKinematicTarget(LeftHandRigidBody, LeftHandTransform);
                    }
                    break;
                case nameof(LeftHandOverlap):
                    LeftHandOverlapChanged?.Invoke(this, prev as IAbstractDynamicRigidBody, LeftHandOverlap);
                    break;
                case nameof(RightHandOverlap):
                    RightHandOverlapChanged?.Invoke(this, prev as IAbstractDynamicRigidBody, RightHandOverlap);
                    break;
            }
        }

        public delegate void DelHandGrabbed(VRPlayerInputSet sender, IAbstractDynamicRigidBody item, bool left);
        public delegate void DelLeftHandGrabbed(VRPlayerInputSet sender, IAbstractDynamicRigidBody item);
        public delegate void DelRightHandGrabbed(VRPlayerInputSet sender, IAbstractDynamicRigidBody item);
        public delegate void DelLeftHandReleased(VRPlayerInputSet sender, IAbstractDynamicRigidBody item);
        public delegate void DelRightHandReleased(VRPlayerInputSet sender, IAbstractDynamicRigidBody item);
        public delegate void DelLeftHandOverlapChanged(VRPlayerInputSet sender, IAbstractDynamicRigidBody? previous, IAbstractDynamicRigidBody? current);
        public delegate void DelRightHandOverlapChanged(VRPlayerInputSet sender, IAbstractDynamicRigidBody? previous, IAbstractDynamicRigidBody? current);

        public event DelHandGrabbed? HandGrabbed;
        public event DelLeftHandGrabbed? LeftHandGrabbed;
        public event DelRightHandGrabbed? RightHandGrabbed;
        public event DelLeftHandReleased? LeftHandReleased;
        public event DelRightHandReleased? RightHandReleased;
        public event DelLeftHandOverlapChanged? LeftHandOverlapChanged;
        public event DelRightHandOverlapChanged? RightHandOverlapChanged;

        private unsafe void RightHandTransform_WorldMatrixChanged(TransformBase tfm, Matrix4x4 worldMatrix)
        {
            //Don't do anything if the hand doesn't exist or is constrained to an item already
            if (RightHandRigidBody is null || RightHandConstraint is not null)
                return;

            SetHandKinematicTarget(RightHandRigidBody, tfm);

            //if (WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene is PhysxScene px)
            //    RightHandOverlap = OverlapTest(tfm, px);
        }

        private unsafe void LeftHandTransform_WorldMatrixChanged(TransformBase tfm, Matrix4x4 worldMatrix)
        {
            //Don't do anything if the hand doesn't exist or is constrained to an item already
            if (LeftHandRigidBody is null || LeftHandConstraint is not null)
                return;

            SetHandKinematicTarget(LeftHandRigidBody, tfm);

            //if (WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene is PhysxScene px)
            //    LeftHandOverlap = OverlapTest(tfm, px);
        }

        private static void SetHandKinematicTarget(
            IAbstractDynamicRigidBody body,
            TransformBase? transform)
            => body.KinematicTarget = (
                transform?.WorldTranslation ?? Vector3.Zero,
                transform?.WorldRotation ?? Quaternion.Identity);

        private unsafe PhysxDynamicRigidBody? OverlapTestPhysxExtension(TransformBase tfm, PhysxScene px)
        {
            var handPos = tfm.WorldTranslation;
            var sphere = new IPhysicsGeometry.Sphere(GrabRadius);
            return px.OverlapAny(sphere, (handPos, Quaternion.Identity), out var hit, PxQueryFlags.Dynamic, null, null) &&
                PhysxDynamicRigidBody.AllDynamic.TryGetValue((nint)hit.actor, out var a) &&
                a is PhysxDynamicRigidBody rb
                ? rb
                : null;
        }

        private void Turn(Vector2 oldValue, Vector2 newValue)
            => CharacterPawn.LookRight(newValue.X);

        private void Locomote(Vector2 oldValue, Vector2 newValue)
        {
            CharacterPawn.MoveRight(newValue.X);
            CharacterPawn.MoveForward(newValue.Y);
        }
    }
}
