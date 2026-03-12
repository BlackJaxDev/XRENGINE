using Extensions;
using System.Numerics;
using XREngine.Data;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Transforms
{
    /// <summary>
    /// Moves the scene node to the parent's position, lagging behind by a specified amount for smooth movement.
    /// Not affected by parent's scale or rotation, as all calculations are done in world space.
    /// </summary>
    public class WorldTranslationLaggedTransform : TransformBase
    {
        public WorldTranslationLaggedTransform() : this(null) { }
        public WorldTranslationLaggedTransform(TransformBase? parent)
            : this(parent, 20.0f, 2.0f) { }
        public WorldTranslationLaggedTransform(TransformBase? parent, float interpSpeed)
            : this(parent, interpSpeed, 2.0f) { }
        public WorldTranslationLaggedTransform(TransformBase? parent, float interpSpeed, float maxLagDistance)
            : base(parent)
        {
            _interpSpeed = interpSpeed;
            _maxLagDistance = maxLagDistance;
        }

        private Vector3 _currentPoint;
        private Vector3 _destPoint;
        private Vector3 _interpPoint;
        private float _interpSpeed;
        private float _maxLagDistance;
        private float _laggingDistance;

        public float InterpSpeed
        {
            get => _interpSpeed;
            set => SetField(ref _interpSpeed, value);
        }
        public float MaxLagDistance
        {
            get => _maxLagDistance;
            set => SetField(ref _maxLagDistance, value);
        }
        public float LaggingDistance
        {
            get => _laggingDistance;
            private set => SetField(ref _laggingDistance, value);
        }

        protected override Matrix4x4 CreateLocalMatrix()
        {
            //We're not using the local matrix for this component
            return Matrix4x4.Identity;
        }
        protected override Matrix4x4 CreateWorldMatrix()
            => Matrix4x4.CreateTranslation(_interpPoint);
        protected internal void Tick()
        {
            _currentPoint = WorldMatrix.Translation;
            _destPoint = ParentWorldMatrix.Translation;
            LaggingDistance = _destPoint.Distance(_currentPoint);
            
            //if (_laggingDistance > _maxLagDistance)
            //    _interpPoint = CustomMath.InterpLinearTo(_destPoint, _currentPoint, _maxLagDistance / _laggingDistance);
            //else
                _interpPoint = Interp.Lerp(_currentPoint, _destPoint, Engine.SmoothedDelta, InterpSpeed);
            MarkWorldModified();
        }
        protected override void OnSceneNodeActivated()
        {
            _currentPoint = WorldMatrix.Translation;
            RegisterTick(ETickGroup.Normal, (int)ETickOrder.Scene, Tick);
        }
        protected override void OnSceneNodeDeactivated()
        {
            UnregisterTick(ETickGroup.Normal, (int)ETickOrder.Scene, Tick);
        }
    }
}
