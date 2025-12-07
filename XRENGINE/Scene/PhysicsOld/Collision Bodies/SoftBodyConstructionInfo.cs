using MemoryPack;
using System.Numerics;
using XREngine.Core.Files;

namespace XREngine.Physics
{
    /// <summary>
    /// Contains parameters for constructing a new soft body.
    /// </summary>
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class SoftBodyConstructionInfo : XRAsset, ICollisionObjectConstructionInfo
    {
        private bool _collisionEnabled = true;
        private bool _simulatePhysics = true;

        private ECollisionGroup _collisionGroup = ECollisionGroup.Default;
        private ECollisionGroup _collidesWith = ECollisionGroup.All;

        private float _waterOffset;
        private Vector3 _waterNormal;
        private float _waterDensity;
        private float _maxDisplacement;
        private Vector3 gravity;
        private float airDensity;
        private XRCollisionShape? _collisionShape;
        private Matrix4x4 _initialWorldTransform;

        public float WaterOffset
        {
            get => _waterOffset;
            set => SetField(ref _waterOffset, value);
        }
        public Vector3 WaterNormal
        {
            get => _waterNormal;
            set => SetField(ref _waterNormal, value);
        }
        public float WaterDensity
        {
            get => _waterDensity;
            set => SetField(ref _waterDensity, value);
        }
        //public SparseSdf SparseSdf { get; }
        public float MaxDisplacement
        {
            get => _maxDisplacement;
            set => SetField(ref _maxDisplacement, value);
        }
        public Vector3 Gravity
        {
            get => gravity;
            set => SetField(ref gravity, value);
        }
        public float AirDensity
        {
            get => airDensity;
            set => SetField(ref airDensity, value);
        }
        public XRCollisionShape? CollisionShape
        {
            get => _collisionShape;
            set => SetField(ref _collisionShape, value);
        }
        public Matrix4x4 InitialWorldTransform
        {
            get => _initialWorldTransform;
            set => SetField(ref _initialWorldTransform, value);
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
        public ECollisionGroup CollisionGroup
        {
            get => _collisionGroup;
            set => SetField(ref _collisionGroup, value);
        }
        public ECollisionGroup CollidesWith
        {
            get => _collidesWith;
            set => SetField(ref _collidesWith, value);
        }
    }
}
