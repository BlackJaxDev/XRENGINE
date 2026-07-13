using System.Numerics;
using JoltPhysicsSharp;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Scene.Physics.Jolt
{
    /// <summary>
    /// Represents a character controller that uses a virtual capsule for movement and collision detection within the Jolt physics engine.
    /// This controller handles movement, collision detection, and response for a character represented by a capsule shape.
    /// </summary>
    public sealed class JoltCharacterVirtualController : IJoltCharacterController
    {
        private readonly CharacterMotionBuffer _motionBuffer = new();

        private int _released;

        private Vector3 _position;
        private Vector3 _upDirection = Globals.Up;

        private float _radius = 0.25f;
        private float _totalHeight = 1.8f;
        private float _slopeLimit = MathF.Cos(MathF.PI / 4.0f);
        private float _stepOffset = 0.3f;
        private float _contactOffset = 0.02f;
        private float _predictiveContactDistance = 0.1f;
        private float _collisionTolerance = 0.001f;
        private float _stickToFloorDistance = 0.1f;
        private float _stepDownExtra;
        private float _mass = 70.0f;
        private float _maxStrength = 100.0f;
        private bool _slideOnSteepSlopes;

        private bool _collidingUp;
        private bool _collidingDown;
        private bool _collidingSides;

        private CharacterMotionInputModel _motionInputModel = CharacterMotionInputModel.Velocity;
        private CharacterMotionCommand _lastMotionCommand;
        private Vector3 _requestedVelocity;
        private Vector3 _effectiveVelocity;
        private CharacterSupportState _supportState = CharacterSupportState.Unknown;
        private Vector3 _groundNormal = Globals.Up;
        private Vector3 _groundVelocity;
        private IAbstractRigidPhysicsActor? _groundActor;
        private bool _wasSupported;
        private Vector3 _lastSupportedGroundVelocity;
        private Vector3 _inheritedGroundVelocity;

        private CapsuleShape? _shape;
        private bool _shapeDirty = true;
        private bool _recreateCharacter;
        private bool _poseDirty = true;
        private bool _contactsDirty = true;
        private float _lastFixedDelta = 1.0f / 60.0f;

        private CharacterVirtual? _character;
        private ObjectLayer _objectLayer;
        private LayerMask _collisionLayerMask = LayerMask.Everything;

        /// <summary>
        /// Initializes a new instance of the <see cref="JoltCharacterVirtualController"/> class with the specified scene and initial position.
        /// </summary>
        /// <param name="scene">The Jolt physics scene in which the character controller will be registered.</param>
        /// <param name="position">The initial position of the character controller.</param>
        public JoltCharacterVirtualController(JoltScene scene, Vector3 position)
            : this(scene, position, Globals.Up)
        {
        }

        /// <summary>
        /// Initializes a controller with its authored up direction without
        /// treating creation as a runtime reorientation/foot-preservation edit.
        /// </summary>
        public JoltCharacterVirtualController(JoltScene scene, Vector3 position, Vector3 upDirection)
        {
            if (!IsFinite(position))
                throw new ArgumentException("Character position must contain only finite values.", nameof(position));
            if (!IsFinite(upDirection) || upDirection.LengthSquared() < 1e-8f)
                throw new ArgumentException("Character up direction must be finite and non-zero.", nameof(upDirection));

            Scene = scene;
            _position = position;
            _upDirection = Vector3.Normalize(upDirection);
            _groundNormal = _upDirection;
            _objectLayer = _collisionLayerMask.AsJoltObjectLayer();
            Scene.RegisterCharacterController(this);
        }

        /// <summary>
        /// Gets the Jolt physics scene in which the character controller is registered.
        /// </summary>
        public JoltScene Scene { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the character controller has been released from the scene.
        /// </summary>
        public bool IsReleased => Volatile.Read(ref _released) != 0;

        /// <summary>
        /// Gets or sets the current position of the character controller.
        /// </summary>
        public Vector3 Position
        {
            get => _position;
            set
            {
                if (!IsFinite(value))
                    return;
                _position = value;
                _poseDirty = true;
                _contactsDirty = true;
            }
        }

        /// <summary>
        /// Gets or sets the position of the character controller's foot.
        /// </summary>
        /// <remarks>
        /// The foot position is calculated based on the character controller's current position, up direction, height, radius, and contact offset.
        /// </remarks>
        public Vector3 FootPosition
        {
            // Total extent from capsule center to foot = half cylinder height + hemisphere radius + skin padding
            get => _position - UpDirection * (TotalHeight * 0.5f + ContactOffset);
            set => Position = value + UpDirection * CapsuleExtent;
        }

        /// <summary>
        /// Gets or sets the up direction of the character controller.
        /// </summary>
        public Vector3 UpDirection
        {
            get => _upDirection;
            set
            {
                if (!IsFinite(value) || value.LengthSquared() < 1e-8f)
                    return;
                Vector3 normalized = Vector3.Normalize(value);
                if (Vector3.DistanceSquared(_upDirection, normalized) < 1e-10f)
                    return;

                Vector3 footPosition = FootPosition;
                _upDirection = normalized;
                Position = footPosition + _upDirection * CapsuleExtent;
                if (_character is not null)
                {
                    _poseDirty = true;
                    _contactsDirty = true;
                }
            }
        }

        /// <summary>
        /// Gets or sets the radius of the character controller's capsule.
        /// </summary>
        public float Radius
        {
            get => _radius;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float v = MathF.Max(0.001f, value);
                if (MathF.Abs(_radius - v) < 1e-6f)
                    return;
                Vector3 footPosition = FootPosition;
                _radius = v;
                _totalHeight = MathF.Max(_totalHeight, 2.0f * _radius);
                _shapeDirty = true;
                if (_character is not null)
                {
                    _recreateCharacter = true;
                    Position = footPosition + UpDirection * CapsuleExtent;
                    _contactsDirty = true;
                }
            }
        }

        /// <summary>
        /// Gets or sets the height of the character controller's capsule.
        /// </summary>
        public float TotalHeight
        {
            get => _totalHeight;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float v = MathF.Max(2.0f * Radius, value);
                if (MathF.Abs(_totalHeight - v) < 1e-6f)
                    return;
                Vector3 footPosition = FootPosition;
                _totalHeight = v;
                _shapeDirty = true;
                if (_character is not null)
                {
                    Position = footPosition + UpDirection * CapsuleExtent;
                    _contactsDirty = true;
                }
            }
        }

        public float CylinderHeight => MathF.Max(0.0f, TotalHeight - 2.0f * Radius);

        /// <summary>
        /// Gets or sets the slope limit of the character controller.
        /// </summary>
        public float SlopeLimit
        {
            get => _slopeLimit;
            set
            {
                if (!float.IsFinite(value))
                    return;
                _slopeLimit = Math.Clamp(value, -1.0f, 1.0f);
            }
        }

        /// <summary>
        /// Gets or sets the step offset of the character controller.
        /// </summary>
        public float StepOffset
        {
            get => _stepOffset;
            set
            {
                if (float.IsFinite(value))
                    _stepOffset = MathF.Max(0.0f, value);
            }
        }

        /// <summary>
        /// Gets or sets the contact offset of the character controller.
        /// </summary>
        public float ContactOffset
        {
            get => _contactOffset;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float v = MathF.Max(0.0f, value);
                if (MathF.Abs(_contactOffset - v) < 1e-6f)
                    return;
                Vector3 footPosition = FootPosition;
                _contactOffset = v;
                _shapeDirty = true;
                if (_character is not null)
                {
                    _recreateCharacter = true;
                    Position = footPosition + UpDirection * CapsuleExtent;
                    _contactsDirty = true;
                }
            }
        }

        public float PredictiveContactDistance
        {
            get => _predictiveContactDistance;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float sanitized = MathF.Max(0.0f, value);
                if (MathF.Abs(_predictiveContactDistance - sanitized) < 1e-6f)
                    return;
                _predictiveContactDistance = sanitized;
                _shapeDirty = true;
                _recreateCharacter = true;
            }
        }

        public float CollisionTolerance
        {
            get => _collisionTolerance;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float sanitized = MathF.Max(0.000001f, value);
                if (MathF.Abs(_collisionTolerance - sanitized) < 1e-6f)
                    return;
                _collisionTolerance = sanitized;
                _shapeDirty = true;
                _recreateCharacter = true;
            }
        }

        public float StickToFloorDistance
        {
            get => _stickToFloorDistance;
            set
            {
                if (float.IsFinite(value))
                    _stickToFloorDistance = MathF.Max(0.0f, value);
            }
        }

        public float StepDownExtra
        {
            get => _stepDownExtra;
            set
            {
                if (float.IsFinite(value))
                    _stepDownExtra = MathF.Max(0.0f, value);
            }
        }

        public float Mass
        {
            get => _mass;
            set
            {
                if (!float.IsFinite(value))
                    return;
                _mass = MathF.Max(0.001f, value);
            }
        }

        public float MaxStrength
        {
            get => _maxStrength;
            set
            {
                if (!float.IsFinite(value))
                    return;
                _maxStrength = MathF.Max(0.0f, value);
            }
        }

        public LayerMask CollisionLayerMask
        {
            get => _collisionLayerMask;
            set
            {
                _collisionLayerMask = value;
                _objectLayer = value.AsJoltObjectLayer();
                _contactsDirty = true;
            }
        }

        public bool SlideOnSteepSlopes
        {
            get => _slideOnSteepSlopes;
            set => _slideOnSteepSlopes = value;
        }

        public CharacterMotionInputModel MotionInputModel
        {
            get => _motionInputModel;
            set => _motionInputModel = value;
        }

        public PhysicsCharacterControllerCapabilities Capabilities
            => PhysicsCharacterControllerCapabilities.DisplacementInput
                | PhysicsCharacterControllerCapabilities.VelocityInput
                | PhysicsCharacterControllerCapabilities.ArbitraryUp
                | PhysicsCharacterControllerCapabilities.MovingGround
                | PhysicsCharacterControllerCapabilities.DynamicBodyInteraction
                | PhysicsCharacterControllerCapabilities.MaximumStrength
                | PhysicsCharacterControllerCapabilities.PredictiveContacts
                | PhysicsCharacterControllerCapabilities.IndependentCollisionTolerance
                | PhysicsCharacterControllerCapabilities.FloorStickDistance
                | PhysicsCharacterControllerCapabilities.IndependentStepDown
                | PhysicsCharacterControllerCapabilities.SteepSlopeSliding
                | PhysicsCharacterControllerCapabilities.CollisionFiltering;

        public CharacterSupportState SupportState => _supportState;
        public bool IsGrounded => _supportState == CharacterSupportState.Supported;
        public Vector3 GroundNormal => _groundNormal;
        public Vector3 GroundVelocity => _groundVelocity;
        public IAbstractRigidPhysicsActor? GroundActor => _groundActor;
        public CharacterMotionCommand LastMotionCommand => _lastMotionCommand;
        public Vector3 RequestedVelocity => _requestedVelocity;
        public Vector3 EffectiveVelocity => _effectiveVelocity;

        /// <summary>
        /// Gets a value indicating whether the character controller is colliding with an object above it.
        /// </summary>
        public bool CollidingUp => _collidingUp;
        /// <summary>
        /// Gets a value indicating whether the character controller is colliding with an object below it.
        /// </summary>
        public bool CollidingDown => _collidingDown;
        /// <summary>
        /// Gets a value indicating whether the character controller is colliding with an object on its sides.
        /// </summary>
        public bool CollidingSides => _collidingSides;

        /// <summary>
        /// Gets the number of active contacts the character controller currently has.
        /// </summary>
        internal int ActiveContactCount => _character?.GetNumActiveContacts() ?? 0;
        /// <summary>
        /// Gets the current ground state of the character controller.
        /// </summary>
        internal GroundState GroundState => _character?.GroundState ?? JoltPhysicsSharp.GroundState.InAir;
        /// <summary>
        /// Gets a value indicating whether the character controller is currently supported by the ground.
        /// </summary>
        internal bool IsSupported => _character?.IsSupported ?? false;
        /// <summary>
        /// Gets the last consumed displacement of the character controller.
        /// </summary>
        internal Vector3 LastConsumedDisplacement { get; private set; }

        /// <summary>
        /// Gets the extent of the character controller's capsule, including the height, radius, and contact offset.
        /// </summary>
        private float CapsuleExtent => TotalHeight * 0.5f + ContactOffset;

        #region IAbstractRigidPhysicsActor

        /// <summary>
        /// Gets the transform of the character controller, including its position and rotation.
        /// </summary>
        public (Vector3 position, Quaternion rotation) Transform
            => (Position, XRMath.RotationBetweenVectors(Globals.Up, UpDirection));

        /// <summary>
        /// Gets the linear velocity of the character controller.
        /// </summary>
        public Vector3 LinearVelocity => _effectiveVelocity;

        /// <summary>
        /// Gets the angular velocity of the character controller.
        /// </summary>
        public Vector3 AngularVelocity => Vector3.Zero;

        /// <summary>
        /// Gets a value indicating whether the character controller is currently sleeping.
        /// </summary>
        public bool IsSleeping => false;

        /// <summary>
        /// Destroys the character controller, optionally waking it on lost touch.
        /// </summary>
        /// <param name="wakeOnLostTouch">Indicates whether to wake the character controller on lost touch.</param>
        public void Destroy(bool wakeOnLostTouch = false)
            => RequestRelease();

        #endregion

        /// <summary>
        /// Resizes the character controller to the specified height.
        /// </summary>
        /// <param name="height">The new height for the character controller.</param>
        public void Resize(float totalHeight)
        {
            Vector3 footPosition = FootPosition;
            TotalHeight = totalHeight;
            FootPosition = footPosition;
        }

        /// <summary>
        /// Moves the character controller by the specified delta, considering the minimum distance and elapsed time.
        /// </summary>
        /// <param name="delta">The movement delta for the character controller.</param>
        /// <param name="minDist">The minimum distance to consider for movement.</param>
        /// <param name="elapsedTime">The elapsed time since the last movement.</param>
        public void SubmitMotion(in CharacterMotionCommand command)
        {
            if (IsReleased)
                return;

            if (_motionBuffer.Enqueue(command))
                _lastMotionCommand = command;
        }

        public void Move(Vector3 value, float minDist, float elapsedTime)
            => SubmitMotion(new CharacterMotionCommand(
                value,
                MotionInputModel,
                minDist,
                elapsedTime));

        /// <summary>
        /// Consumes the input buffer and applies the accumulated movement to the character controller for the given fixed delta time.
        /// </summary>
        /// <param name="fixedDelta">The fixed delta time for the physics update.</param>
        public void ConsumeInputBuffer(float fixedDelta)
        {
            if (IsReleased)
                return;
            if (!float.IsFinite(fixedDelta) || fixedDelta <= 0.0f)
                return;

            _lastFixedDelta = fixedDelta;

            EnsureCharacterExists();
            if (_character is null)
                return;

            ApplyPendingPoseAndRefreshContacts();
            _character.UpdateGroundVelocity();

            bool supportedBeforeUpdate = _character.IsSupported;
            Vector3 groundVelocityBeforeUpdate = supportedBeforeUpdate
                ? _character.GroundVelocity
                : Vector3.Zero;
            if (supportedBeforeUpdate)
            {
                _lastSupportedGroundVelocity = groundVelocityBeforeUpdate;
                _inheritedGroundVelocity = Vector3.Zero;
            }
            else if (_wasSupported)
            {
                _inheritedGroundVelocity = _lastSupportedGroundVelocity;
            }

            CharacterMotionStep motion = _motionBuffer.Consume(fixedDelta);
            Vector3 requestedDisplacement = motion.Displacement;
            if (requestedDisplacement.LengthSquared() <= motion.MinDistance * motion.MinDistance)
                requestedDisplacement = Vector3.Zero;

            LastConsumedDisplacement = requestedDisplacement;
            _requestedVelocity = requestedDisplacement / fixedDelta;

            Vector3 desiredVelocity = _requestedVelocity;
            desiredVelocity += supportedBeforeUpdate
                ? groundVelocityBeforeUpdate
                : _inheritedGroundVelocity;
            if (!SlideOnSteepSlopes)
                desiredVelocity = _character.CancelVelocityTowardsSteepSlopes(in desiredVelocity);
            _character.LinearVelocity = desiredVelocity;

            _character.MaxSlopeAngle = MathF.Acos(SlopeLimit);
            _character.Mass = Mass;
            _character.MaxStrength = MaxStrength;

            ExtendedUpdateSettings settings = new()
            {
                StickToFloorStepDown = -UpDirection * StickToFloorDistance,
                WalkStairsStepUp = UpDirection * StepOffset,
                WalkStairsStepDownExtra = -UpDirection * StepDownExtra,
            };

            ObjectLayer layer = _objectLayer;
            BodyFilter? bodyFilter = null;
            ShapeFilter? shapeFilter = null;

            Vector3 startPos = _position;
            _character.ExtendedUpdate(
                fixedDelta,
                settings,
                in layer,
                Scene.PhysicsSystem!,
                bodyFilter,
                shapeFilter);

            _position = _character.Position;
            RefreshCollisionFlagsFromContacts();
            RefreshSupportState();
            _effectiveVelocity = (_position - startPos) / fixedDelta;

            bool supportedAfterUpdate = _character.IsSupported;
            if (supportedBeforeUpdate && !supportedAfterUpdate)
                _inheritedGroundVelocity = groundVelocityBeforeUpdate;
            else if (supportedAfterUpdate)
                _inheritedGroundVelocity = Vector3.Zero;
            _wasSupported = supportedAfterUpdate;
        }

        /// <summary>
        /// Requests the release and disposal of the character controller, cleaning up associated resources.
        /// After calling this method, the character controller should no longer be used.
        /// </summary>
        /// <remarks>
        /// This method is thread-safe and can be called from any thread.
        /// </remarks>
        public void RequestRelease()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            _motionBuffer.Clear();

            _character?.Dispose();
            _character = null;

            _shape?.Dispose();
            _shape = null;

            Scene.UnregisterCharacterController(this);
        }

        /// <summary>
        /// Ensures that the character controller exists and is properly initialized.
        /// If the character controller or its shape is missing or marked as dirty, it will be recreated.
        /// </summary>
        private void EnsureCharacterExists()
        {
            if (_character is not null && !_shapeDirty)
                return;

            if (Scene.PhysicsSystem is null)
                return;

            if (_character is not null && _recreateCharacter)
            {
                _character.Dispose();
                _character = null;
                _shape?.Dispose();
                _shape = null;
            }

            CapsuleShape replacementShape = new(CylinderHeight * 0.5f, Radius);

            Quaternion capsuleRotation = XRMath.RotationBetweenVectors(Globals.Up, UpDirection);

            if (_character is null)
            {
                CharacterVirtualSettings settings = new()
                {
                    Shape = replacementShape,
                    Up = UpDirection,
                    SupportingVolume = new Plane(Vector3.UnitY, -Radius),
                    MaxSlopeAngle = MathF.Acos(SlopeLimit),
                    CharacterPadding = ContactOffset,
                    PredictiveContactDistance = PredictiveContactDistance,
                    CollisionTolerance = CollisionTolerance,
                    Mass = Mass,
                    MaxStrength = MaxStrength,
                    ShapeOffset = Vector3.Zero,
                };

                _character = new CharacterVirtual(settings, in _position, in capsuleRotation, 0, Scene.PhysicsSystem)
                {
                    Up = UpDirection,
                    MaxSlopeAngle = MathF.Acos(SlopeLimit),
                    Mass = Mass,
                    MaxStrength = MaxStrength,
                };
                _shape = replacementShape;
                _poseDirty = false;
                _contactsDirty = true;
            }
            else
            {
                ObjectLayer layer = _objectLayer;
                BodyFilter? bodyFilter = null;
                ShapeFilter? shapeFilter = null;

                if (_character.SetShape(
                    _lastFixedDelta,
                    replacementShape,
                    CollisionTolerance,
                    in layer,
                    Scene.PhysicsSystem,
                    bodyFilter,
                    shapeFilter))
                {
                    CapsuleShape? previousShape = _shape;
                    _shape = replacementShape;
                    previousShape?.Dispose();
                    _contactsDirty = true;
                }
                else
                {
                    replacementShape.Dispose();
                    return;
                }
            }

            _shapeDirty = false;
            _recreateCharacter = false;
        }

        private void ApplyPendingPoseAndRefreshContacts()
        {
            if (_character is null || Scene.PhysicsSystem is null)
                return;

            if (_poseDirty)
            {
                _character.Up = UpDirection;
                _character.Rotation = XRMath.RotationBetweenVectors(Globals.Up, UpDirection);
                _character.Position = _position;
                _poseDirty = false;
                _contactsDirty = true;
            }

            if (!_contactsDirty)
                return;

            ObjectLayer layer = _objectLayer;
            _character.RefreshContacts(in layer, Scene.PhysicsSystem);
            _contactsDirty = false;
        }

        private void RefreshSupportState()
        {
            if (_character is null)
            {
                _supportState = CharacterSupportState.Unknown;
                _groundNormal = UpDirection;
                _groundVelocity = Vector3.Zero;
                _groundActor = null;
                return;
            }

            _supportState = _character.GroundState switch
            {
                JoltPhysicsSharp.GroundState.OnGround => CharacterSupportState.Supported,
                JoltPhysicsSharp.GroundState.OnSteepGround => CharacterSupportState.TooSteep,
                JoltPhysicsSharp.GroundState.NotSupported => CharacterSupportState.NotSupported,
                JoltPhysicsSharp.GroundState.InAir => CharacterSupportState.InAir,
                _ => CharacterSupportState.Unknown,
            };

            Vector3 normal = _character.GroundNormal;
            _groundNormal = normal.LengthSquared() > 1e-8f
                ? Vector3.Normalize(normal)
                : UpDirection;
            _groundVelocity = _character.GroundVelocity;

            BodyID groundBodyId = _character.GroundBodyId;
            _groundActor = groundBodyId.IsInvalid
                ? null
                : Scene.GetRigidActor(groundBodyId);
        }

        /// <summary>
        /// Updates the collision flags based on the current active contacts of the character controller.
        /// </summary>
        private void RefreshCollisionFlagsFromContacts()
        {
            _collidingUp = false;
            _collidingDown = false;
            _collidingSides = false;

            if (_character is null)
                return;

            int contactCount = _character.GetNumActiveContacts();
            if (contactCount <= 0)
                return;

            for (int index = 0; index < contactCount; index++)
            {
                CharacterVirtual.Contact c = _character.GetActiveContact(index);
                if (!c.HadCollision || c.IsSensorB)
                    continue;
                
                float upDot = Vector3.Dot(c.ContactNormal, UpDirection);
                if (upDot > 0.5f)
                    _collidingDown = true;
                else if (upDot < -0.5f)
                    _collidingUp = true;
                else
                    _collidingSides = true;

                if (_collidingUp && _collidingDown && _collidingSides)
                    break;
            }
        }

        private static bool IsFinite(in Vector3 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    }
}
