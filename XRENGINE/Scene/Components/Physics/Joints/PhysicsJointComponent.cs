using System.ComponentModel;
using System.Numerics;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene;
using XREngine.Scene.Physics.Joints;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Physics
{
    /// <summary>
    /// Base class for all physics joint/constraint components.
    /// Manages the lifecycle of a native joint: creation on activation, destruction on deactivation,
    /// and rebinding when connected bodies change.
    /// </summary>
    [Category("Physics")]
    public abstract class PhysicsJointComponent : XRComponent, IRenderable
    {
        private PhysicsActorComponent? _connectedBody;
        private Vector3 _anchorPosition = Vector3.Zero;
        private Quaternion _anchorRotation = Quaternion.Identity;
        private Vector3 _connectedAnchorPosition = Vector3.Zero;
        private Quaternion _connectedAnchorRotation = Quaternion.Identity;
        private bool _autoConfigureConnectedAnchor = true;
        private float _breakForce = float.MaxValue;
        private float _breakTorque = float.MaxValue;
        private bool _enableCollision;
        private bool _enablePreprocessing = true;
        private IAbstractJoint? _nativeJoint;
        private bool _drawGizmos = true;

        private readonly RenderInfo3D _gizmoRenderInfo;

        protected PhysicsJointComponent()
        {
            _gizmoRenderInfo = RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OnTopForward, RenderJointGizmos));
            _gizmoRenderInfo.Layer = DefaultLayers.GizmosIndex;
            RenderedObjects = [_gizmoRenderInfo];
        }

        /// <summary>
        /// When true, debug gizmos are drawn for this joint in the editor viewport.
        /// </summary>
        [Category("Debug")]
        [DisplayName("Draw Gizmos")]
        [Description("Show debug gizmos for anchors, axes, and limits.")]
        public bool DrawGizmos
        {
            get => _drawGizmos;
            set => SetField(ref _drawGizmos, value);
        }

        /// <inheritdoc/>
        public RenderInfo[] RenderedObjects { get; }

        /// <summary>
        /// Fired when the joint breaks due to exceeding break force/torque thresholds.
        /// </summary>
        public event Action<PhysicsJointComponent>? JointBroken;

        /// <summary>
        /// The other rigid body this joint connects to.
        /// If null, the joint connects to the world frame.
        /// </summary>
        [Category("Joint")]
        [DisplayName("Connected Body")]
        [Description("The other physics actor this joint connects to. Null means anchored to the world.")]
        public PhysicsActorComponent? ConnectedBody
        {
            get => _connectedBody;
            set
            {
                if (SetField(ref _connectedBody, value))
                    RebindJoint();
            }
        }

        /// <summary>
        /// Local-space anchor position on this body (actor A).
        /// </summary>
        [Category("Anchors")]
        [DisplayName("Anchor Position")]
        [Description("Anchor position in this body's local space.")]
        public Vector3 AnchorPosition
        {
            get => _anchorPosition;
            set
            {
                if (SetField(ref _anchorPosition, value))
                    PushAnchorsToNative();
            }
        }

        /// <summary>
        /// Local-space anchor rotation on this body (actor A).
        /// </summary>
        [Category("Anchors")]
        [DisplayName("Anchor Rotation")]
        [Description("Anchor rotation in this body's local space.")]
        public Quaternion AnchorRotation
        {
            get => _anchorRotation;
            set
            {
                if (SetField(ref _anchorRotation, value))
                    PushAnchorsToNative();
            }
        }

        /// <summary>
        /// Local-space anchor position on the connected body (actor B).
        /// </summary>
        [Category("Anchors")]
        [DisplayName("Connected Anchor Position")]
        [Description("Anchor position in the connected body's local space.")]
        public Vector3 ConnectedAnchorPosition
        {
            get => _connectedAnchorPosition;
            set
            {
                if (SetField(ref _connectedAnchorPosition, value))
                    PushAnchorsToNative();
            }
        }

        /// <summary>
        /// Local-space anchor rotation on the connected body (actor B).
        /// </summary>
        [Category("Anchors")]
        [DisplayName("Connected Anchor Rotation")]
        [Description("Anchor rotation in the connected body's local space.")]
        public Quaternion ConnectedAnchorRotation
        {
            get => _connectedAnchorRotation;
            set
            {
                if (SetField(ref _connectedAnchorRotation, value))
                    PushAnchorsToNative();
            }
        }

        /// <summary>
        /// When true, the connected anchor is automatically computed from the initial
        /// relative transforms of the two bodies at joint creation time.
        /// </summary>
        [Category("Anchors")]
        [DisplayName("Auto-Configure Connected Anchor")]
        [Description("Automatically compute the connected anchor from initial body transforms.")]
        public bool AutoConfigureConnectedAnchor
        {
            get => _autoConfigureConnectedAnchor;
            set => SetField(ref _autoConfigureConnectedAnchor, value);
        }

        /// <summary>
        /// Maximum force the joint can sustain before breaking. MaxValue = unbreakable.
        /// </summary>
        [Category("Break")]
        [DisplayName("Break Force")]
        [Description("Maximum force before the joint breaks. MaxValue means unbreakable.")]
        public float BreakForce
        {
            get => _breakForce;
            set
            {
                if (SetField(ref _breakForce, value))
                    PushBreakThresholds();
            }
        }

        /// <summary>
        /// Maximum torque the joint can sustain before breaking. MaxValue = unbreakable.
        /// </summary>
        [Category("Break")]
        [DisplayName("Break Torque")]
        [Description("Maximum torque before the joint breaks. MaxValue means unbreakable.")]
        public float BreakTorque
        {
            get => _breakTorque;
            set
            {
                if (SetField(ref _breakTorque, value))
                    PushBreakThresholds();
            }
        }

        /// <summary>
        /// Whether collision between the connected bodies is enabled.
        /// </summary>
        [Category("Joint")]
        [DisplayName("Enable Collision")]
        [Description("Allow collision between the two connected bodies.")]
        public bool EnableCollision
        {
            get => _enableCollision;
            set
            {
                if (SetField(ref _enableCollision, value) && _nativeJoint is not null)
                    _nativeJoint.EnableCollision = value;
            }
        }

        /// <summary>
        /// Enable constraint preprocessing for improved stability.
        /// </summary>
        [Category("Joint")]
        [DisplayName("Enable Preprocessing")]
        [Description("Enable constraint preprocessing for improved stability.")]
        public bool EnablePreprocessing
        {
            get => _enablePreprocessing;
            set
            {
                if (SetField(ref _enablePreprocessing, value) && _nativeJoint is not null)
                    _nativeJoint.EnablePreprocessing = value;
            }
        }

        /// <summary>
        /// The underlying native joint. Null when the component is not active.
        /// </summary>
        [Browsable(false)]
        [RuntimeOnly]
        public IAbstractJoint? NativeJoint => _nativeJoint;

        #region Lifecycle

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            CreateNativeJoint();
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            DestroyNativeJoint();
        }

        /// <summary>
        /// Destroys and recreates the native joint. Called when connected body references change.
        /// </summary>
        protected void RebindJoint()
        {
            if (!IsActiveInHierarchy)
                return;

            DestroyNativeJoint();
            CreateNativeJoint();
        }

        private void CreateNativeJoint()
        {
            var scene = World?.PhysicsScene;
            if (scene is null)
                return;

            var actorA = ResolveLocalActor();
            var actorB = ResolveConnectedActor();

            var localFrameA = new JointAnchor(AnchorPosition, AnchorRotation);
            JointAnchor localFrameB;

            if (AutoConfigureConnectedAnchor)
                localFrameB = ComputeAutoConnectedAnchor(actorA, actorB, localFrameA);
            else
                localFrameB = new JointAnchor(ConnectedAnchorPosition, ConnectedAnchorRotation);

            _nativeJoint = CreateJointImpl(scene, actorA, localFrameA, actorB, localFrameB);

            if (_nativeJoint is not null)
            {
                scene.RegisterJointComponent(_nativeJoint, this);
                _nativeJoint.BreakForce = _breakForce;
                _nativeJoint.BreakTorque = _breakTorque;
                _nativeJoint.EnableCollision = _enableCollision;
                _nativeJoint.EnablePreprocessing = _enablePreprocessing;
                ApplyJointProperties(_nativeJoint);
            }
        }

        private void DestroyNativeJoint()
        {
            if (_nativeJoint is null)
                return;

            var scene = World?.PhysicsScene;
            if (scene is not null)
            {
                scene.UnregisterJointComponent(_nativeJoint);
                scene.RemoveJoint(_nativeJoint);
            }
            else
            {
                _nativeJoint.Release();
            }

            _nativeJoint = null;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Resolves the physics actor on the same scene node (actor A / "this body").
        /// </summary>
        private IAbstractPhysicsActor? ResolveLocalActor()
        {
            if (TryGetSiblingComponent<DynamicRigidBodyComponent>(out var dyn) && dyn?.RigidBody is not null)
                return dyn.RigidBody;
            if (TryGetSiblingComponent<StaticRigidBodyComponent>(out var stat) && stat?.RigidBody is not null)
                return stat.RigidBody;
            return null;
        }

        /// <summary>
        /// Resolves the physics actor for the connected body (actor B).
        /// Returns null when anchored to the world.
        /// </summary>
        private IAbstractPhysicsActor? ResolveConnectedActor()
        {
            if (_connectedBody is null)
                return null;

            if (_connectedBody is DynamicRigidBodyComponent dyn)
                return dyn.RigidBody;
            if (_connectedBody is StaticRigidBodyComponent stat)
                return stat.RigidBody;
            return null;
        }

        /// <summary>
        /// Computes an auto-configured connected anchor by converting the local anchor
        /// from actor A's space into actor B's local space (or world space if B is null).
        /// </summary>
        private JointAnchor ComputeAutoConnectedAnchor(
            IAbstractPhysicsActor? actorA,
            IAbstractPhysicsActor? actorB,
            JointAnchor localFrameA)
        {
            // Get actor A world transform
            var nodeA = SceneNode;
            Matrix4x4 worldA = nodeA.Transform.WorldMatrix;

            // Compute world-space anchor
            Matrix4x4 anchorLocal = Matrix4x4.CreateFromQuaternion(localFrameA.Rotation)
                                  * Matrix4x4.CreateTranslation(localFrameA.Position);
            Matrix4x4 anchorWorld = anchorLocal * worldA;

            if (_connectedBody is not null)
            {
                // Convert into connected body local space
                Matrix4x4 worldB = _connectedBody.Transform.WorldMatrix;
                if (Matrix4x4.Invert(worldB, out Matrix4x4 invWorldB))
                {
                    Matrix4x4 anchorInB = anchorWorld * invWorldB;
                    Matrix4x4.Decompose(anchorInB, out _, out Quaternion rot, out Vector3 pos);
                    return new JointAnchor(pos, rot);
                }
            }

            // Anchored to world: use world-space position directly
            Matrix4x4.Decompose(anchorWorld, out _, out Quaternion worldRot, out Vector3 worldPos);
            return new JointAnchor(worldPos, worldRot);
        }

        private void PushAnchorsToNative()
        {
            if (_nativeJoint is null)
                return;

            _nativeJoint.LocalFrameA = new JointAnchor(AnchorPosition, AnchorRotation);

            if (!AutoConfigureConnectedAnchor)
                _nativeJoint.LocalFrameB = new JointAnchor(ConnectedAnchorPosition, ConnectedAnchorRotation);
        }

        private void PushBreakThresholds()
        {
            if (_nativeJoint is null)
                return;

            _nativeJoint.BreakForce = _breakForce;
            _nativeJoint.BreakTorque = _breakTorque;
        }

        /// <summary>
        /// Called by the physics backend when the joint breaks.
        /// </summary>
        internal void NotifyJointBroken()
        {
            JointBroken?.Invoke(this);
            DestroyNativeJoint();
        }

        #endregion

        #region Gizmo Rendering

        /// <summary>
        /// Renders debug gizmos: anchor points, connecting line, and joint-specific visualization.
        /// </summary>
        private void RenderJointGizmos()
        {
            if (!_drawGizmos || !IsActiveInHierarchy || Engine.Rendering.State.IsShadowPass)
                return;

            Matrix4x4 worldA = Transform.WorldMatrix;
            Vector3 anchorWorldA = Vector3.Transform(_anchorPosition, worldA);

            Vector3 anchorWorldB;
            if (_connectedBody is not null)
            {
                Matrix4x4 worldB = _connectedBody.Transform.WorldMatrix;
                anchorWorldB = Vector3.Transform(_connectedAnchorPosition, worldB);
            }
            else
            {
                anchorWorldB = _connectedAnchorPosition;
            }

            // Draw anchor points
            Engine.Rendering.Debug.RenderSphere(anchorWorldA, 0.025f, false, ColorF4.Cyan);
            Engine.Rendering.Debug.RenderSphere(anchorWorldB, 0.025f, false, ColorF4.Magenta);

            // Draw connecting line between anchors
            Engine.Rendering.Debug.RenderLine(anchorWorldA, anchorWorldB, NativeJoint is null ? ColorF4.Red : ColorF4.Green);

            // Draw local axes at anchor A
            DrawAnchorAxes(anchorWorldA, worldA, _anchorRotation, 0.1f);

            // Subclass-specific gizmo overlays
            RenderJointSpecificGizmos(anchorWorldA, anchorWorldB, worldA);
        }

        /// <summary>
        /// Draws local X (red), Y (green), Z (blue) axes at an anchor location.
        /// </summary>
        private static void DrawAnchorAxes(Vector3 worldPos, Matrix4x4 bodyWorld, Quaternion anchorRot, float length)
        {
            Matrix4x4 rotMatrix = Matrix4x4.CreateFromQuaternion(anchorRot);
            Matrix4x4 combinedRot = rotMatrix * bodyWorld;
            // Extract rotation-only directions
            Vector3 right = Vector3.Normalize(new Vector3(combinedRot.M11, combinedRot.M12, combinedRot.M13));
            Vector3 up = Vector3.Normalize(new Vector3(combinedRot.M21, combinedRot.M22, combinedRot.M23));
            Vector3 forward = Vector3.Normalize(new Vector3(combinedRot.M31, combinedRot.M32, combinedRot.M33));

            Engine.Rendering.Debug.RenderLine(worldPos, worldPos + right * length, ColorF4.Red);
            Engine.Rendering.Debug.RenderLine(worldPos, worldPos + up * length, ColorF4.Green);
            Engine.Rendering.Debug.RenderLine(worldPos, worldPos + forward * length, ColorF4.Blue);
        }

        /// <summary>
        /// Overridden by subclasses to draw joint-type-specific gizmos (limit arcs, cones, etc.).
        /// </summary>
        protected virtual void RenderJointSpecificGizmos(Vector3 anchorWorldA, Vector3 anchorWorldB, Matrix4x4 bodyWorldA)
        {
        }

        #endregion

        #region Abstract

        /// <summary>
        /// Subclasses implement this to call the appropriate factory method on the physics scene.
        /// </summary>
        protected abstract IAbstractJoint? CreateJointImpl(
            AbstractPhysicsScene scene,
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB);

        /// <summary>
        /// Called after the native joint is created. Subclasses push their specific properties here.
        /// </summary>
        protected abstract void ApplyJointProperties(IAbstractJoint joint);

        #endregion
    }
}
