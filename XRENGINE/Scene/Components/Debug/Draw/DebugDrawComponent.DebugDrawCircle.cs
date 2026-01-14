using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    public partial class DebugDrawComponent
    {
        public class DebugDrawCircle(float radius, Vector3 localOffset, Vector3 localNormal, ColorF4 color, bool solid) : DebugShapeBase(color, solid)
        {
            /// <summary>
            /// Parameterless constructor for serialization.
            /// </summary>
            public DebugDrawCircle() : this(1.0f, Vector3.Zero, Vector3.UnitY, ColorF4.White, false) { }

            /// <summary>
            /// The local offset of the circle's center relative to this component's transform.
            /// </summary>
            public Vector3 LocalOffset
            {
                get => localOffset;
                set => SetField(ref localOffset, value);
            }

            /// <summary>
            /// The local normal of the circle's plane relative to this component's transform.
            /// </summary>
            public Vector3 LocalNormal
            {
                get => localNormal;
                set => SetField(ref localNormal, value);
            }

            /// <summary>
            /// The radius of the circle.
            /// </summary>
            public float Radius
            {
                get => radius;
                set => SetField(ref radius, value);
            }

            public override void Render(TransformBase transform)
                => Engine.Rendering.Debug.RenderCircle(
                    transform.TransformPoint(LocalOffset, true),
                    XRMath.RotationBetweenVectors(transform.TransformDirection(LocalNormal, true), Globals.Up),
                    Radius,
                    Solid,
                    Color);
        }
    }
}