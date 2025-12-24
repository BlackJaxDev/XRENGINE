using System.Collections.Concurrent;
using System.Numerics;
using JoltPhysicsSharp;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Scene.Physics.Jolt
{
    public sealed class JoltCharacterVirtualController : IJoltCharacterController
    {
        private readonly ConcurrentQueue<(Vector3 delta, float minDist, float elapsedTime)> _inputBuffer = new();

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
                if (_character is not null)
                    _character.Position = value;
            }
        }

        public Vector3 FootPosition
        {
            // Total extent from capsule center to foot = half cylinder height + hemisphere radius + skin padding
            get => _position - UpDirection * (Height * 0.5f + Radius + ContactOffset);
            set => _position = value + UpDirection * (Height * 0.5f + Radius + ContactOffset);
        }

        public Vector3 UpDirection
        {
            get => _upDirection;
            set
            {
                if (value.LengthSquared() < 1e-8f)
                    return;
                _upDirection = Vector3.Normalize(value);
            }
        }

        public float Radius
        {
            get => _radius;
            set
            {
                float v = MathF.Max(0.0f, value);
                if (MathF.Abs(_radius - v) < 1e-6f)
                    return;
                _radius = v;
                _shapeDirty = true;
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
                _height = v;
                _shapeDirty = true;
            }
        }

        public float SlopeLimit
        {
            get => _slopeLimit;
            set => _slopeLimit = value;
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
                _contactOffset = v;
                _shapeDirty = true;
            }
        }

        public bool CollidingUp => _collidingUp;
        public bool CollidingDown => _collidingDown;
        public bool CollidingSides => _collidingSides;

        #region IAbstractRigidPhysicsActor

        public (Vector3 position, Quaternion rotation) Transform => (Position, Quaternion.Identity);

        public Vector3 LinearVelocity => _linearVelocity;

        public Vector3 AngularVelocity => Vector3.Zero;

        public bool IsSleeping => false;

        public void Destroy(bool wakeOnLostTouch = false) => RequestRelease();

        #endregion

        public void Resize(float height) => Height = height;

        public void Move(Vector3 delta, float minDist, float elapsedTime)
        {
            if (IsReleased)
                return;

            if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y) || !float.IsFinite(delta.Z))
                return;
            if (!float.IsFinite(minDist) || !float.IsFinite(elapsedTime) || elapsedTime <= 0.0f)
                return;

            _inputBuffer.Enqueue((delta, minDist, elapsedTime));
        }

        public void ConsumeInputBuffer(float fixedDelta)
        {
            if (IsReleased)
                return;
            if (fixedDelta <= 0.0f)
                return;

            EnsureCharacterExists();
            if (_character is null)
                return;

            Vector3 startPos = _position;

            _collidingUp = false;
            _collidingDown = false;
            _collidingSides = false;

            Vector3 totalMove = Vector3.Zero;
            while (_inputBuffer.TryDequeue(out var input))
                totalMove += input.delta;

            var desiredVelocity = totalMove / fixedDelta;
            desiredVelocity = _character.CancelVelocityTowardsSteepSlopes(in desiredVelocity);
            _character.LinearVelocity = desiredVelocity;

            Quaternion capsuleRotation = XRMath.RotationBetweenVectors(Globals.Up, UpDirection);
            _character.Rotation = capsuleRotation;
            _character.Position = _position;

            _character.HitReductionCosMaxAngle = SlopeLimit;

            ExtendedUpdateSettings settings = new()
            {
                StickToFloorStepDown = -UpDirection * (StepOffset + ContactOffset),
                WalkStairsStepUp = UpDirection * StepOffset,
                WalkStairsStepDownExtra = -UpDirection * StepOffset,
            };

            ObjectLayer layer = _objectLayer;
            BodyFilter? bodyFilter = null;
            ShapeFilter? shapeFilter = null;

            _character.ExtendedUpdate(fixedDelta, settings, in layer, Scene.PhysicsSystem!, bodyFilter!, shapeFilter!);

            _position = _character.Position;
            RefreshCollisionFlagsFromContacts();

            _linearVelocity = (_position - startPos) / fixedDelta;
        }

        public void RequestRelease()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            while (_inputBuffer.TryDequeue(out _)) { }

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

            UpdateShape();
            if (_shape is null)
                return;

            Quaternion capsuleRotation = XRMath.RotationBetweenVectors(Globals.Up, UpDirection);

            if (_character is null)
            {
                CharacterVirtualSettings settings = new()
                {
                    CharacterPadding = ContactOffset,
                    PredictiveContactDistance = ContactOffset,
                    CollisionTolerance = ContactOffset,
                    InnerBodyShape = _shape,
                    InnerBodyLayer = _objectLayer,
                    ShapeOffset = Vector3.Zero,
                };

                _character = new CharacterVirtual(settings, in _position, in capsuleRotation, 0, Scene.PhysicsSystem)
                {
                    HitReductionCosMaxAngle = SlopeLimit
                };
            }
            else
            {
                ObjectLayer layer = _objectLayer;
                BodyFilter? bodyFilter = null;
                ShapeFilter? shapeFilter = null;

                _character.SetShape(ContactOffset, _shape, ContactOffset, in layer, Scene.PhysicsSystem, bodyFilter!, shapeFilter!);
            }

            _shapeDirty = false;
        }

        private void UpdateShape()
        {
            _shape?.Dispose();
            _shape = null;

            // Jolt capsule is oriented along +Y. Height is cylinder height (excludes hemispheres).
            _shape = new CapsuleShape(Height * 0.5f, Radius);
        }

        private void RefreshCollisionFlagsFromContacts()
        {
            if (_character is null)
                return;

            int contactCount = _character.GetNumActiveContacts();
            if (contactCount <= 0)
                return;

            foreach (var c in _character.GetActiveContacts())
            {
                if (!c.HadCollision)
                    continue;
                if (c.IsSensorB)
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
    }
}
