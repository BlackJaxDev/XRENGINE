using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    public partial class DebugDrawComponent
    {
        public class DebugDrawCylinder(float radius, float halfHeight, Vector3 localOffset, Vector3 localUpAxis, ColorF4 color, bool solid) : DebugShapeBase(color, solid)
        {
            /// <summary>
            /// Parameterless constructor for serialization.
            /// </summary>
            public DebugDrawCylinder() : this(1.0f, 1.0f, Vector3.Zero, Vector3.UnitY, ColorF4.White, false) { }

            /// <summary>
            /// The radius of the cylinder.
            /// </summary>
            public float Radius
            {
                get => radius;
                set => SetField(ref radius, value);
            }

            /// <summary>
            /// Half the height of the cylinder.
            /// </summary>
            public float HalfHeight
            {
                get => halfHeight;
                set => SetField(ref halfHeight, value);
            }

            /// <summary>
            /// The local offset of the cylinder's center relative to this component's transform.
            /// </summary>
            public Vector3 LocalOffset
            {
                get => localOffset;
                set => SetField(ref localOffset, value);
            }

            /// <summary>
            /// The local up axis of the cylinder, relative to this component's transform.
            /// </summary>
            public Vector3 LocalUpAxis
            {
                get => localUpAxis;
                set => SetField(ref localUpAxis, value);
            }

            public override void Render(TransformBase transform)
                => Engine.Rendering.Debug.RenderCylinder(
                    Matrix4x4.CreateTranslation(LocalOffset) * transform.RenderMatrix,
                    LocalUpAxis,
                    Radius,
                    HalfHeight,
                    Solid,
                    Color);
        }
    }
}