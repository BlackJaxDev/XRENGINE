using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Physics;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;

namespace XREngine.Data.Components
{
    public abstract class CommonShape3DComponent<T> : CollidableShape3DComponent where T : IShape, new()
    {
        protected CommonShape3DComponent(T shape, ICollisionObjectConstructionInfo? info = null)
            : base()
        {
            _shape = shape;
            RenderCommand = new RenderCommandMethod3D(0, Render);
            GenerateCollisionObject(info);
        }

        public RenderCommandMethod3D RenderCommand { get; }
        protected T _shape;

        public override RenderInfo3D RenderInfo
        {
            get => base.RenderInfo;
            set
            {
                base.RenderInfo = value;
                RenderInfo.LocalCullingVolume = _shape.GetAABB(false);
                RenderInfo.CullingOffsetMatrix = Transform.WorldMatrix;
                RenderInfo.CastsShadows = false;
                RenderInfo.ReceivesShadows = false;
            }
        }

        public virtual T Shape
        {
            get => _shape;
            set => SetField(ref _shape, value ?? new T());
        }

        protected override void OnPropertyChanged<T2>(string? propName, T2 prev, T2 field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Shape):
                    ShapeChanged();
                    break;
            }
        }

        private void ShapeChanged()
        {
            RenderInfo.LocalCullingVolume = _shape.GetAABB(false);
            RenderInfo.CullingOffsetMatrix = Transform.WorldMatrix;

            if (CollisionObject == null)
                return;
            
            CollisionObject.CollisionShape = GetCollisionShape();
            OnRigidBodyShapeUpdated();
        }

        protected virtual void Render()
        {
            if (Engine.Rendering.State.IsShadowPass)
                return;

            Engine.Rendering.Debug.RenderShape(_shape, false, ColorF4.White);
        }

        protected override RenderCommand3D GetRenderCommand()
            => RenderCommand;

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform)
        {
            base.OnTransformRenderWorldMatrixChanged(transform);
            //_shape?.SetTransformMatrix(WorldMatrix);
        }

        //public override CollisionShape GetCollisionShape()
        //{
        //    //_shape?.GetCollisionShape();
        //}
    }
}
