using System.Numerics;
using JoltPhysicsSharp;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Scene.Physics.Jolt
{
    public sealed class JoltCharacterVirtualController : IJoltCharacterController
    {
        private const int MoveBufferCapacity = 64;

        private readonly object _inputBufferLock = new();
        private readonly ControllerMove[] _inputBuffer = new ControllerMove[MoveBufferCapacity];
        private int _inputHead;
        private int _inputCount;

        private int _released;

        private Vector3 _position;
        private Vector3 _upDirection = Globals.Up;

        private float _radius;
        private float _height;
        private float _slopeLimit;
        private float _stepOffset;
        private float _contactOffset;

        private bool _collidingUp;
        private bool _collidingDown;
        private bool _collidingSides;

        private Vector3 _linearVelocity;

        private CapsuleShape? _shape;
        private bool _shapeDirty = true;
        private bool _recreateCharacter;
        private float _lastFixedDelta = 1.0f / 60.0f;

        private CharacterVirtual? _character;
        private ObjectLayer _objectLayer;

        public JoltCharacterVirtualController(JoltScene scene, Vector3 position)
        {
            Scene = scene;
            _position = position;
            _objectLayer = LayerMask.Everything.AsJoltObjectLayer();
            Scene.RegisterCharacterController(this);
        }

        public JoltScene Scene { get; private set; }

        public bool IsReleased => Volatile.Read(ref _released) != 0;

        public Vector3 Position
        {
            get => _position;
            set
            {
                _position = value;
                _character?.Position = value;
            }
        }

        public Vector3 FootPosition
        {
            // Total extent from capsule center to foot = half cylinder height + hemisphere radius + skin padding
            get => _position - UpDirection * (Height * 0.5f + Radius + ContactOffset);
            set => Position = value + UpDirection * CapsuleExtent;
        }

        public Vector3 UpDirection
        {
            get => _upDirection;
            set
            {
                if (value.LengthSquared() < 1e-8f)
                    return;
                Vector3 normalized = Vector3.Normalize(value);
                if (Vector3.DistanceSquared(_upDirection, normalized) < 1e-10f)
                    return;

                Vector3 footPosition = FootPosition;
                _upDirection = normalized;
                Position = footPosition + _upDirection * CapsuleExtent;
                if (_character is not null)
                {
                    _character.Up = _upDirection;
                    _character.Rotation = XRMath.RotationBetweenVectors(Globals.Up, _upDirection);
                }
            }
        }

        public float Radius
        {
            get => _radius;
            set
            {
                float v = MathF.Max(0.001f, value);
                if (MathF.Abs(_radius - v) < 1e-6f)
                    return;
                Vector3 footPosition = FootPosition;
                _radius = v;
                _shapeDirty = true;
                if (_character is not null)
                {
                    _recreateCharacter = true;
                    Position = footPosition + UpDirection * CapsuleExtent;
                }
            }
        }

        public float Height
        {
            get => _height;
            set
            {
                float v = MathF.Max(0.0f, value);
                if (MathF.Abs(_height - v) < 1e-6f)
                    return;
                Vector3 footPosition = FootPosition;
                _height = v;
                _shapeDirty = true;
                if (_character is not null)
                    Position = footPosition + UpDirection * CapsuleExtent;
            }
        }

        public float SlopeLimit
        {
            get => _slopeLimit;
            set
            {
                _slopeLimit = Math.Clamp(value, -1.0f, 1.0f);
                if (_character is not null)
                    _character.MaxSlopeAngle = MathF.Acos(_slopeLimit);
            }
        }

        public float StepOffset
        {
            get => _stepOffset;
            set => _stepOffset = MathF.Max(0.0f, value);
        }

        public float ContactOffset
        {
            get => _contactOffset;
            set
            {
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
                }
            }
        }

        public bool CollidingUp => _collidingUp;
        public bool CollidingDown => _collidingDown;
        public bool CollidingSides => _collidingSides;

        internal int ActiveContactCount => _character?.GetNumActiveContacts() ?? 0;
        internal GroundState GroundState => _character?.GroundState ?? JoltPhysicsSharp.GroundState.InAir;
        internal bool IsSupported => _character?.IsSupported ?? false;
        internal Vector3 LastConsumedDisplacement { get; private set; }

        private float CapsuleExtent => Height * 0.5f + Radius + ContactOffset;

        #region IAbstractRigidPhysicsActor

        public (Vector3 position, Quaternion rotation) Transform
            => (Position, XRMath.RotationBetweenVectors(Globals.Up, UpDirection));

        public Vector3 LinearVelocity => _linearVelocity;

        public Vector3 AngularVelocity => Vector3.Zero;

        public bool IsSleeping => false;

        public void Destroy(bool wakeOnLostTouch = false) => RequestRelease();

        #endregion

        public void Resize(float height)
        {
            Vector3 footPosition = FootPosition;
            Height = height;
            FootPosition = footPosition;
        }

        public void Move(Vector3 delta, float minDist, float elapsedTime)
        {
            if (IsReleased)
                return;

            if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y) || !float.IsFinite(delta.Z))
                return;
            if (!float.IsFinite(minDist) || !float.IsFinite(elapsedTime) || elapsedTime <= 0.0f)
                return;

            lock (_inputBufferLock)
            {
                ControllerMove move = new(delta, MathF.Max(0.0f, minDist), elapsedTime);
                if (_inputCount == MoveBufferCapacity)
                {
                    int tail = (_inputHead + _inputCount - 1) % MoveBufferCapacity;
                    ControllerMove previous = _inputBuffer[tail];
                    _inputBuffer[tail] = new ControllerMove(
                        previous.Delta + move.Delta,
                        MathF.Max(previous.MinDistance, move.MinDistance),
                        previous.ElapsedTime + move.ElapsedTime);
                    return;
                }

                int index = (_inputHead + _inputCount) % MoveBufferCapacity;
                _inputBuffer[index] = move;
                _inputCount++;
            }
        }

        public void ConsumeInputBuffer(float fixedDelta)
        {
            if (IsReleased)
                return;
            if (fixedDelta <= 0.0f)
                return;

            _lastFixedDelta = fixedDelta;

            EnsureCharacterExists();
            if (_character is null)
                return;

            Vector3 startPos = _position;

            _collidingUp = false;
            _collidingDown = false;
            _collidingSides = false;

            Vector3 totalMove = Vector3.Zero;
            float minimumDistance = 0.0f;
            lock (_inputBufferLock)
            {
                for (int index = 0; index < _inputCount; index++)
                {
                    ControllerMove input = _inputBuffer[(_inputHead + index) % MoveBufferCapacity];
                    totalMove += input.Delta;
                    minimumDistance = MathF.Max(minimumDistance, input.MinDistance);
                }

                _inputHead = 0;
                _inputCount = 0;
            }

            LastConsumedDisplacement = totalMove;

            if (totalMove.LengthSquared() <= minimumDistance * minimumDistance)
            {
                _linearVelocity = Vector3.Zero;
                RefreshCollisionFlagsFromContacts();
                return;
            }

            var desiredVelocity = totalMove / fixedDelta;
            desiredVelocity = _character.CancelVelocityTowardsSteepSlopes(in desiredVelocity);
            _character.LinearVelocity = desiredVelocity;

            Quaternion capsuleRotation = XRMath.RotationBetweenVectors(Globals.Up, UpDirection);
            _character.Rotation = capsuleRotation;
            _character.Position = _position;

            _character.MaxSlopeAngle = MathF.Acos(SlopeLimit);

            ExtendedUpdateSettings settings = new()
            {
                StickToFloorStepDown = -UpDirection * (StepOffset + ContactOffset),
                WalkStairsStepUp = UpDirection * StepOffset,
                WalkStairsStepDownExtra = -UpDirection * StepOffset,
            };

            ObjectLayer layer = _objectLayer;
            BodyFilter? bodyFilter = null;
            ShapeFilter? shapeFilter = null;

            _character.ExtendedUpdate(fixedDelta, settings, in layer, Scene.PhysicsSystem!, bodyFilter, shapeFilter);

            _position = _character.Position;
            RefreshCollisionFlagsFromContacts();

            _linearVelocity = (_position - startPos) / fixedDelta;
        }

        public void RequestRelease()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            lock (_inputBufferLock)
            {
                _inputHead = 0;
                _inputCount = 0;
            }

            _character?.Dispose();
            _character = null;

            _shape?.Dispose();
            _shape = null;

            Scene.UnregisterCharacterController(this);
        }

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

            CapsuleShape replacementShape = new(Height * 0.5f, Radius);

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
                    PredictiveContactDistance = ContactOffset,
                    CollisionTolerance = ContactOffset,
                    ShapeOffset = Vector3.Zero,
                };

                _character = new CharacterVirtual(settings, in _position, in capsuleRotation, 0, Scene.PhysicsSystem)
                {
                    Up = UpDirection,
                    MaxSlopeAngle = MathF.Acos(SlopeLimit),
                };
                _shape = replacementShape;
            }
            else
            {
                ObjectLayer layer = _objectLayer;
                BodyFilter? bodyFilter = null;
                ShapeFilter? shapeFilter = null;

                if (_character.SetShape(_lastFixedDelta, replacementShape, ContactOffset, in layer, Scene.PhysicsSystem, bodyFilter, shapeFilter))
                {
                    CapsuleShape? previousShape = _shape;
                    _shape = replacementShape;
                    previousShape?.Dispose();
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

        private void RefreshCollisionFlagsFromContacts()
        {
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
                if (upDot >= SlopeLimit)
                    _collidingDown = true;
                else if (upDot < -0.5f)
                    _collidingUp = true;
                else
                    _collidingSides = true;

                if (_collidingUp && _collidingDown && _collidingSides)
                    break;
            }
        }

        private readonly record struct ControllerMove(
            Vector3 Delta,
            float MinDistance,
            float ElapsedTime);
    }
}
