using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    public partial class DebugDrawComponent
    {
        public class DebugDrawPoint(Vector3 localOffset, ColorF4 color) : DebugShapeBase(color, false)
        {
            /// <summary>
            /// The local offset of the point relative to this component's transform.
            /// </summary>
            public Vector3 LocalOffset
            {
                get => localOffset;
                set => SetField(ref localOffset, value);
            }

            public override void Render(TransformBase transform)
                => Engine.Rendering.Debug.RenderPoint(transform.TransformPoint(LocalOffset), Color);
        }
    }
}