using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    public partial class DebugDrawComponent
    {
        public class DebugDrawCapsule(float radius, Vector3 localStartOffset, Vector3 localEndOffset, ColorF4 color, bool solid) : DebugShapeBase(color, solid)
        {
            /// <summary>
            /// The radius of the capsule.
            /// </summary>
            public float Radius
            {
                get => radius;
                set => SetField(ref radius, value);
            }
            /// <summary>
            /// The local offset of the capsule's center relative to this component's transform.
            /// </summary>
            public Vector3 LocalStartOffset
            {
                get => localStartOffset;
                set => SetField(ref localStartOffset, value);
            }
            /// <summary>
            /// The local offset of the capsule's end relative to this component's transform.
            /// </summary>
            public Vector3 LocalEndOffset
            {
                get => localEndOffset;
                set => SetField(ref localEndOffset, value);
            }

            public override void Render(TransformBase transform)
                => Engine.Rendering.Debug.RenderCapsule(
                    transform.TransformPoint(LocalStartOffset, true),
                    transform.TransformPoint(LocalEndOffset, true),
                    Radius,
                    Solid,
                    Color);
        }
    }
}