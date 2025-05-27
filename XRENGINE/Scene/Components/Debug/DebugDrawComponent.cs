using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Components
{
    public partial class DebugDrawComponent : XRComponent, IRenderable
    {
        public DebugDrawComponent()
        {
            RenderInfo3D ri = RenderInfo3D.New(this);
            ri.RenderCommands.Add(new RenderCommandMethod3D(EDefaultRenderPass.OnTopForward, Render));
            RenderedObjects = [ri];
        }

        public void Render()
        {
            foreach (var shape in Shapes)
                shape.Render(Transform);
        }

        private EventList<DebugShapeBase> _shapes = [];
        public EventList<DebugShapeBase> Shapes
        {
            get => _shapes;
            set => SetField(ref _shapes, value);
        }

        public RenderInfo[] RenderedObjects { get; }

        public void AddSphere(float radius, Vector3 localOffset, ColorF4 color, bool solid)
            => Shapes.Add(new DebugDrawSphere(radius, localOffset, color, solid));
        public void AddBox(Vector3 halfExtents, Vector3 localOffset, ColorF4 color, bool solid)
            => Shapes.Add(new DebugDrawBox(halfExtents, localOffset, color, solid));
        public void AddCircle(float radius, Vector3 localOffset, Vector3 localNormal, ColorF4 color, bool solid)
            => Shapes.Add(new DebugDrawCircle(radius, localOffset, localNormal, color, solid));
        public void AddCapsule(float radius, Vector3 localStartOffset, Vector3 localEndOffset, ColorF4 color, bool solid)
            => Shapes.Add(new DebugDrawCapsule(radius, localStartOffset, localEndOffset, color, solid));
        public void AddCone(float radius, float height, Vector3 localOffset, Vector3 localUpAxis, ColorF4 color, bool solid)
            => Shapes.Add(new DebugDrawCone(radius, height, localOffset, localUpAxis, color, solid));
        public void AddCylinder(float radius, float halfHeight, Vector3 localOffset, Vector3 localUpAxis, ColorF4 color, bool solid)
            => Shapes.Add(new DebugDrawCylinder(radius, halfHeight, localOffset, localUpAxis, color, solid));
        public void AddLine(Vector3 localStartOffset, Vector3 localEndOffset, ColorF4 color)
            => Shapes.Add(new DebugDrawLine(localStartOffset, localEndOffset, color));
        public void AddPoint(Vector3 localOffset, ColorF4 color)
            => Shapes.Add(new DebugDrawPoint(localOffset, color));

        public void AddShape(DebugShapeBase shape)
            => Shapes.Add(shape);
        public void ClearShapes()
            => Shapes.Clear();
    }
}