using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Components
{
    public partial class DebugDrawComponent : XRComponent, IRenderable
    {
        private readonly object _shapeSync = new();
        private readonly Stack<DebugDrawSphere> _spherePool = [];
        private readonly Stack<DebugDrawBox> _boxPool = [];
        private readonly Stack<DebugDrawCircle> _circlePool = [];
        private readonly Stack<DebugDrawCapsule> _capsulePool = [];
        private readonly Stack<DebugDrawCone> _conePool = [];
        private readonly Stack<DebugDrawCylinder> _cylinderPool = [];
        private readonly Stack<DebugDrawLine> _linePool = [];
        private readonly Stack<DebugDrawPoint> _pointPool = [];
        private readonly HashSet<DebugShapeBase> _pooledShapes = [];

        public DebugDrawComponent()
        {
            RenderInfo3D ri = RenderInfo3D.New(this);
            ri.RenderCommands.Add(new RenderCommandMethod3D(EDefaultRenderPass.OnTopForward, Render));
            RenderedObjects = [ri];
        }

        public void Render()
        {
            using var profilerState = Engine.Profiler.Start("DebugDrawComponent.Render");

            lock (_shapeSync)
            {
                var shapes = Shapes;
                if (shapes.Count == 0)
                    return;

                for (int i = 0; i < shapes.Count; i++)
                    shapes[i]?.Render(Transform);
            }
        }

        private EventList<DebugShapeBase> _shapes = [];
        public EventList<DebugShapeBase> Shapes
        {
            get => _shapes ??= [];
            set => SetField(ref _shapes, value ?? []);
        }

        public RenderInfo[] RenderedObjects { get; }

        public void AddSphere(float radius, Vector3 localOffset, ColorF4 color, bool solid)
        {
            lock (_shapeSync)
            {
                var shape = RentShape(_spherePool);
                shape.Radius = radius;
                shape.LocalOffset = localOffset;
                shape.Color = color;
                shape.Solid = solid;
                Shapes.Add(shape);
            }
        }
        public void AddBox(Vector3 halfExtents, Vector3 localOffset, ColorF4 color, bool solid)
        {
            lock (_shapeSync)
            {
                var shape = RentShape(_boxPool);
                shape.HalfExtents = halfExtents;
                shape.LocalOffset = localOffset;
                shape.Color = color;
                shape.Solid = solid;
                Shapes.Add(shape);
            }
        }
        public void AddCircle(float radius, Vector3 localOffset, Vector3 localNormal, ColorF4 color, bool solid)
        {
            lock (_shapeSync)
            {
                var shape = RentShape(_circlePool);
                shape.Radius = radius;
                shape.LocalOffset = localOffset;
                shape.LocalNormal = localNormal;
                shape.Color = color;
                shape.Solid = solid;
                Shapes.Add(shape);
            }
        }
        public void AddCapsule(float radius, Vector3 localStartOffset, Vector3 localEndOffset, ColorF4 color, bool solid)
        {
            lock (_shapeSync)
            {
                var shape = RentShape(_capsulePool);
                shape.Radius = radius;
                shape.LocalStartOffset = localStartOffset;
                shape.LocalEndOffset = localEndOffset;
                shape.Color = color;
                shape.Solid = solid;
                Shapes.Add(shape);
            }
        }
        public void AddCone(float radius, float height, Vector3 localOffset, Vector3 localUpAxis, ColorF4 color, bool solid)
        {
            lock (_shapeSync)
            {
                var shape = RentShape(_conePool);
                shape.Radius = radius;
                shape.Height = height;
                shape.LocalOffset = localOffset;
                shape.LocalUpAxis = localUpAxis;
                shape.Color = color;
                shape.Solid = solid;
                Shapes.Add(shape);
            }
        }
        public void AddCylinder(float radius, float halfHeight, Vector3 localOffset, Vector3 localUpAxis, ColorF4 color, bool solid)
        {
            lock (_shapeSync)
            {
                var shape = RentShape(_cylinderPool);
                shape.Radius = radius;
                shape.HalfHeight = halfHeight;
                shape.LocalOffset = localOffset;
                shape.LocalUpAxis = localUpAxis;
                shape.Color = color;
                shape.Solid = solid;
                Shapes.Add(shape);
            }
        }
        public void AddLine(Vector3 localStartOffset, Vector3 localEndOffset, ColorF4 color)
        {
            lock (_shapeSync)
            {
                var shape = RentShape(_linePool);
                shape.StartOffset = localStartOffset;
                shape.EndOffset = localEndOffset;
                shape.Color = color;
                Shapes.Add(shape);
            }
        }
        public void AddPoint(Vector3 localOffset, ColorF4 color)
        {
            lock (_shapeSync)
            {
                var shape = RentShape(_pointPool);
                shape.LocalOffset = localOffset;
                shape.Color = color;
                Shapes.Add(shape);
            }
        }

        public void AddShape(DebugShapeBase shape)
        {
            lock (_shapeSync)
                Shapes.Add(shape);
        }
        public void ClearShapes()
        {
            lock (_shapeSync)
            {
                var shapes = Shapes;
                for (int i = 0; i < shapes.Count; i++)
                {
                    DebugShapeBase? shape = shapes[i];
                    if (shape is not null && _pooledShapes.Contains(shape))
                        ReturnShape(shape);
                }

                shapes.Clear();
            }
        }

        private T RentShape<T>(Stack<T> pool) where T : DebugShapeBase, new()
        {
            T shape = pool.Count > 0 ? pool.Pop() : new T();
            _pooledShapes.Add(shape);
            return shape;
        }

        private void ReturnShape(DebugShapeBase shape)
        {
            switch (shape)
            {
                case DebugDrawSphere sphere:
                    _spherePool.Push(sphere);
                    break;
                case DebugDrawBox box:
                    _boxPool.Push(box);
                    break;
                case DebugDrawCircle circle:
                    _circlePool.Push(circle);
                    break;
                case DebugDrawCapsule capsule:
                    _capsulePool.Push(capsule);
                    break;
                case DebugDrawCone cone:
                    _conePool.Push(cone);
                    break;
                case DebugDrawCylinder cylinder:
                    _cylinderPool.Push(cylinder);
                    break;
                case DebugDrawLine line:
                    _linePool.Push(line);
                    break;
                case DebugDrawPoint point:
                    _pointPool.Push(point);
                    break;
            }
        }
    }
}
