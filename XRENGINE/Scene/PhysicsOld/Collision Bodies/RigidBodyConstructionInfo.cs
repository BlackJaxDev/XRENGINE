using MemoryPack;
using System.ComponentModel;
using System.Numerics;
using XREngine.Core.Files;

namespace XREngine.Physics
{
    /// <summary>
    /// Contains parameters for constructing a new rigid body.
    /// </summary>
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class RigidBodyConstructionInfo : XRAsset, ICollisionObjectConstructionInfo
    {
        public RigidBodyConstructionInfo() { }
        public RigidBodyConstructionInfo(
            XRCollisionShape shape,
            float mass,
            Vector3? localIntertia,
            bool useMotionState,
            ushort collisionGroup,
            ushort collidesWith,
            bool collisionEnabled,
            bool simulatePhysics,
            bool sleepingEnabled,
            float deactivationTime)
        {
            CollisionShape = shape;
            Mass = mass;
            LocalInertia = localIntertia ?? shape?.CalculateLocalInertia(mass) ?? Vector3.Zero;
            UseMotionState = useMotionState;
            CollisionGroup = collisionGroup;
            CollidesWith = collidesWith;
            CollisionEnabled = collisionEnabled;
            SimulatePhysics = simulatePhysics;
            SleepingEnabled = sleepingEnabled;
            DeactivationTime = deactivationTime;
        }

        public bool SleepingEnabled
        {
            get => _sleepingEnabled;
            set => SetField(ref _sleepingEnabled, value);
        }
        public bool CollisionEnabled
        {
            get => _collisionEnabled;
            set => SetField(ref _collisionEnabled, value);
        }
        public bool SimulatePhysics
        {
            get => _simulatePhysics;
            set => SetField(ref _simulatePhysics, value);
        }

        /// <summary>
        /// Use <see cref="ECollisionGroup"/> or your own enum if you want. Note that the enum must be flags.
        /// </summary>
        public ushort CollisionGroup
        {
            get => _collisionGroup;
            set => SetField(ref _collisionGroup, value);
        }
        /// <summary>
        /// Use <see cref="ECollisionGroup"/> or your own enum if you want. Note that the enum must be flags.
        /// </summary>
        public ushort CollidesWith
        {
            get => _collidesWith;
            set => SetField(ref _collidesWith, value);
        }

        public bool UseMotionState
        {
            get => _useMotionState;
            set => SetField(ref _useMotionState, value);
        }
        public Matrix4x4 CenterOfMassOffset
        {
            get => _centerOfMassOffset;
            set => SetField(ref _centerOfMassOffset, value);
        }
        public Matrix4x4 InitialWorldTransform
        {
            get => _initialWorldTransform;
            set => SetField(ref _initialWorldTransform, value);
        }

        public bool AdditionalDamping
        {
            get => _additionalDamping;
            set => SetField(ref _additionalDamping, value);
        }
        public float AdditionalDampingFactor
        {
            get => _additionalDampingFactor;
            set => SetField(ref _additionalDampingFactor, value);
        }
        public float AdditionalLinearDampingThresholdSqr
        {
            get => _additionalLinearDampingThresholdSqr;
            set => SetField(ref _additionalLinearDampingThresholdSqr, value);
        }
        public float AngularDamping
        {
            get => _angularDamping;
            set => SetField(ref _angularDamping, value);
        }
        public float AngularSleepingThreshold
        {
            get => _angularSleepingThreshold;
            set => SetField(ref _angularSleepingThreshold, value);
        }
        public float Friction
        {
            get => _friction;
            set => SetField(ref _friction, value);
        }
        public float LinearDamping
        {
            get => _linearDamping;
            set => SetField(ref _linearDamping, value);
        }
        public float AdditionalAngularDampingThresholdSqr
        {
            get => _additionalAngularDampingThresholdSqr;
            set => SetField(ref _additionalAngularDampingThresholdSqr, value);
        }
        public float Restitution
        {
            get => _restitution;
            set => SetField(ref _restitution, value);
        }
        public float RollingFriction
        {
            get => _rollingFriction;
            set => SetField(ref _rollingFriction, value);
        }
        public float LinearSleepingThreshold
        {
            get => _linearSleepingThreshold;
            set => SetField(ref _linearSleepingThreshold, value);
        }
        public float AdditionalAngularDampingFactor
        {
            get => _additionalAngularDampingFactor;
            set => SetField(ref _additionalAngularDampingFactor, value);
        }
        public bool IsKinematic
        {
            get => _isKinematic;
            set => SetField(ref _isKinematic, value);
        }
        public bool CustomMaterialCallback
        {
            get => _customMaterialCallback;
            set => SetField(ref _customMaterialCallback, value);
        }
        public float CcdMotionThreshold
        {
            get => _ccdMotionThreshold;
            set => SetField(ref _ccdMotionThreshold, value);
        }
        public float DeactivationTime
        {
            get => _deactivationTime;
            set => SetField(ref _deactivationTime, value);
        }
        public float CcdSweptSphereRadius
        {
            get => _ccdSweptSphereRadius;
            set => SetField(ref _ccdSweptSphereRadius, value);
        }
        public float ContactProcessingThreshold
        {
            get => _contactProcessingThreshold;
            set => SetField(ref _contactProcessingThreshold, value);
        }
        /// <summary>
        /// Inertia vector relative to the rigid body's local frame space. 
        /// Auto-calculated when you set CollisionShape or Mass (using both), 
        /// so set after setting those if you want to override.
        /// </summary>
        [Description("Inertia vector relative to the rigid body's local frame space. " +
            "Auto-calculated when you set CollisionShape or Mass (requires both to calculate), " +
            "so set after setting those if you want to override.")]
        public Vector3 LocalInertia
        {
            get => _localInertia;
            set => SetField(ref _localInertia, value);
        }
        /// <summary>
        /// The shape this rigid body will use to collide.
        /// Auto-calculates LocalInertia for you using Mass and the given shape.
        /// </summary>
        public XRCollisionShape? CollisionShape
        {
            get => _collisionShape;
            set
            {
                SetField(ref _collisionShape, value);
                if (CollisionShape != null)
                    LocalInertia = CollisionShape.CalculateLocalInertia(Mass);
            }
        }
        /// <summary>
        /// The mass of this rigid body.
        /// Auto-calculates LocalIntertia for you using CollisionShape and the given mass.
        /// </summary>
        public float Mass
        {
            get => _mass;
            set
            {
                SetField(ref _mass, value);
                if (CollisionShape != null)
                    LocalInertia = CollisionShape.CalculateLocalInertia(Mass);
            }
        }

        private float _mass = 1.0f;
        private XRCollisionShape? _collisionShape = null;
        private bool _sleepingEnabled = true;
        private bool _collisionEnabled = true;
        private bool _simulatePhysics = true;
        private ushort _collisionGroup = 1;
        private ushort _collidesWith = 0xFFFF;
        private bool _useMotionState = true;
        private Matrix4x4 _centerOfMassOffset = Matrix4x4.Identity;
        private Matrix4x4 _initialWorldTransform = Matrix4x4.Identity;
        private bool _additionalDamping = false;
        private float _additionalDampingFactor = 0.005f;
        private float _additionalLinearDampingThresholdSqr = 0.01f;
        private float _angularDamping = 0.0f;
        private float _angularSleepingThreshold = 1.0f;
        private float _friction = 0.5f;
        private float _linearDamping = 0.0f;
        private float _additionalAngularDampingThresholdSqr = 0.01f;
        private float _restitution = 0.0f;
        private float _rollingFriction = 0.0f;
        private float _linearSleepingThreshold = 0.8f;
        private float _additionalAngularDampingFactor = 0.01f;
        private bool _isKinematic = false;
        private bool _customMaterialCallback = true;
        private float _ccdMotionThreshold = 0.0f;
        private float _deactivationTime = 0.0f;
        private float _ccdSweptSphereRadius = 0.0f;
        private float _contactProcessingThreshold = 0.0f;
        private Vector3 _localInertia = Vector3.Zero;
    }
}
