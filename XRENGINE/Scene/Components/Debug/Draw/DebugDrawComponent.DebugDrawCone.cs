using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    public partial class DebugDrawComponent
    {
        public class DebugDrawCone(float radius, float height, Vector3 localOffset, Vector3 localUpAxis, ColorF4 color, bool solid) : DebugShapeBase(color, solid)
        {
            /// <summary>
            /// Parameterless constructor for serialization.
            /// </summary>
            public DebugDrawCone() : this(1.0f, 1.0f, Vector3.Zero, Vector3.UnitY, ColorF4.White, false) { }

            /// <summary>
            /// The radius of the cone.
            /// </summary>
            public float Radius
            {
                get => radius;
                set => SetField(ref radius, value);
            }

            /// <summary>
            /// The height of the cone.
            /// </summary>
            public float Height
            {
                get => height;
                set => SetField(ref height, value);
            }

            /// <summary>
            /// The local up axis of the cone, defining its orientation.
            /// </summary>
            public Vector3 LocalUpAxis
            {
                get => localUpAxis;
                set => SetField(ref localUpAxis, value);
            }

            /// <summary>
            /// The local offset of the cone's center relative to this component's transform.
            /// </summary>
            public Vector3 LocalOffset
            {
                get => localOffset;
                set => SetField(ref localOffset, value);
            }

            public override void Render(TransformBase transform)
                => Engine.Rendering.Debug.RenderCone(
                    transform.TransformPoint(LocalOffset),
                    transform.TransformDirection(LocalUpAxis),
                    Radius,
                    Height,
                    Solid,
                    Color);
        }
    }
}