using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    private class Particle(Transform? transform, int parentIndex) : XRBase
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
        private bool _isCollide;

        internal Vector3 _position;
        private Vector3 _prevPosition;
        private Vector3 _endOffset;
        private Vector3 _initLocalPosition;
        private Quaternion _initLocalRotation;

        private Vector3 _transformPosition;
        private Vector3 _transformLocalPosition;
        private Matrix4x4 _transformLocalToWorldMatrix;

        public int ChildCount
        {
            get => _childCount;
            set => SetFieldUnchecked(ref _childCount, value);
        }
        public float Damping
        {
            get => _damping;
            set => SetFieldUnchecked(ref _damping, value);
        }
        public float Elasticity
        {
            get => _elasticity;
            set => SetFieldUnchecked(ref _elasticity, value);
        }
        public float Stiffness
        {
            get => _stiffness;
            set => SetFieldUnchecked(ref _stiffness, value);
        }
        public float Inert
        {
            get => _inert;
            set => SetFieldUnchecked(ref _inert, value);
        }
        public float Friction
        {
            get => _friction;
            set => SetFieldUnchecked(ref _friction, value);
        }
        public float Radius
        {
            get => _radius;
            set => SetFieldUnchecked(ref _radius, value);
        }
        public float BoneLength
        {
            get => _boneLength;
            set => SetFieldUnchecked(ref _boneLength, value);
        }
        public bool IsColliding
        {
            get => _isCollide;
            set => SetFieldUnchecked(ref _isCollide, value);
        }
        public Vector3 Position
        {
            get => _position;
            set => SetFieldUnchecked(ref _position, value);
        }
        public Vector3 PrevPosition
        {
            get => _prevPosition;
            set => SetFieldUnchecked(ref _prevPosition, value);
        }
        public Vector3 EndOffset
        {
            get => _endOffset;
            set => SetFieldUnchecked(ref _endOffset, value);
        }
        public Vector3 InitLocalPosition
        {
            get => _initLocalPosition;
            set => SetFieldUnchecked(ref _initLocalPosition, value);
        }
        public Quaternion InitLocalRotation
        {
            get => _initLocalRotation;
            set => SetFieldUnchecked(ref _initLocalRotation, value);
        }
        public Vector3 TransformPosition
        {
            get => _transformPosition;
            set => SetFieldUnchecked(ref _transformPosition, value);
        }
        public Vector3 TransformLocalPosition
        {
            get => _transformLocalPosition;
            set => SetFieldUnchecked(ref _transformLocalPosition, value);
        }
        public Matrix4x4 TransformLocalToWorldMatrix
        {
            get => _transformLocalToWorldMatrix;
            set => SetFieldUnchecked(ref _transformLocalToWorldMatrix, value);
        }
    }
}
