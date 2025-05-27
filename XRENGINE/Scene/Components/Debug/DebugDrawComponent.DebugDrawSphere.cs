using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    public partial class DebugDrawComponent
    {
        public class DebugDrawSphere(float radius, Vector3 localOffset, ColorF4 color, bool solid) : DebugShapeBase(color, solid)
        {
            /// <summary>
            /// The radius of the sphere.
            /// </summary>
            public float Radius
            {
                get => radius;
                set => SetField(ref radius, value);
            }

            /// <summary>
            /// The local offset of the sphere's center relative to this component's transform.
            /// </summary>
            public Vector3 LocalOffset
            {
                get => localOffset;
                set => SetField(ref localOffset, value);
            }

            public override void Render(TransformBase transform)
                => Engine.Rendering.Debug.RenderSphere(
                    transform.TransformPoint(LocalOffset, true),
                    Radius,
                    Solid,
                    Color);
        }
    }
}