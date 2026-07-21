using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    /// <summary>
    /// Runtime particle state. This deliberately does not derive from
    /// <see cref="XRBase"/>: solver writes are internal state transitions, not
    /// authored property changes, and must not enter notification machinery.
    /// </summary>
    private sealed class Particle(Transform? transform, int parentIndex)
    {
        public Transform? Transform { get; } = transform;
        public int ParentIndex { get; } = parentIndex;

        private int _childCount;
        private float _damping;
        private float _elasticity;
        private float _stiffness;
        private float _inert;
        private float _friction;
        private float _radius = 0.01f;
        private float _boneLength;
        private float _segmentLength;
        private bool _isCollide;

        internal Vector3 _position;
        private Vector3 _prevPosition;
        private Vector3 _previousPhysicsPosition;
        private Vector3 _endOffset;
        private Vector3 _initLocalPosition;
        private Quaternion _initLocalRotation;

        private Vector3 _transformPosition;
        private Vector3 _transformLocalPosition;
        private Matrix4x4 _transformLocalToWorldMatrix;
        private bool _preparedWorldChanged;

        public int ChildCount
        {
            get => _childCount;
            set => _childCount = value;
        }
        public float Damping
        {
            get => _damping;
            set => _damping = value;
        }
        public float Elasticity
        {
            get => _elasticity;
            set => _elasticity = value;
        }
        public float Stiffness
        {
            get => _stiffness;
            set => _stiffness = value;
        }
        public float Inert
        {
            get => _inert;
            set => _inert = value;
        }
        public float Friction
        {
            get => _friction;
            set => _friction = value;
        }
        public float Radius
        {
            get => _radius;
            set => _radius = value;
        }
        public float BoneLength
        {
            get => _boneLength;
            set => _boneLength = value;
        }
        public float SegmentLength
        {
            get => _segmentLength;
            set => _segmentLength = value;
        }
        public bool IsColliding
        {
            get => _isCollide;
            set => _isCollide = value;
        }
        public Vector3 Position
        {
            get => _position;
            set => _position = value;
        }
        public Vector3 PrevPosition
        {
            get => _prevPosition;
            set => _prevPosition = value;
        }
        public Vector3 PreviousPhysicsPosition
        {
            get => _previousPhysicsPosition;
            set => _previousPhysicsPosition = value;
        }
        public Vector3 EndOffset
        {
            get => _endOffset;
            set => _endOffset = value;
        }
        public Vector3 InitLocalPosition
        {
            get => _initLocalPosition;
            set => _initLocalPosition = value;
        }
        public Quaternion InitLocalRotation
        {
            get => _initLocalRotation;
            set => _initLocalRotation = value;
        }
        public Vector3 TransformPosition
        {
            get => _transformPosition;
            set => _transformPosition = value;
        }
        public Vector3 TransformLocalPosition
        {
            get => _transformLocalPosition;
            set => _transformLocalPosition = value;
        }
        public Matrix4x4 TransformLocalToWorldMatrix
        {
            get => _transformLocalToWorldMatrix;
            set => _transformLocalToWorldMatrix = value;
        }
        public bool PreparedWorldChanged
        {
            get => _preparedWorldChanged;
            set => _preparedWorldChanged = value;
        }
    }
}
