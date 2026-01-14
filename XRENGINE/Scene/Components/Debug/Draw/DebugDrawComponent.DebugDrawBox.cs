using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    public partial class DebugDrawComponent
    {
        public class DebugDrawBox(Vector3 halfExtents, Vector3 localOffset, ColorF4 color, bool solid) : DebugShapeBase(color, solid)
        {
            /// <summary>
            /// Parameterless constructor for serialization.
            /// </summary>
            public DebugDrawBox() : this(Vector3.One, Vector3.Zero, ColorF4.White, false) { }

            /// <summary>
            /// Half of the size of the box in each dimension.
            /// </summary>
            public Vector3 HalfExtents
            {
                get => halfExtents;
                set => SetField(ref halfExtents, value);
            }

            /// <summary>
            /// The local offset of the box's center relative to this component's transform.
            /// </summary>
            public Vector3 LocalOffset
            {
                get => localOffset;
                set => SetField(ref localOffset, value);
            }

            public override void Render(TransformBase transform)
                => Engine.Rendering.Debug.RenderBox(
                    HalfExtents,
                    LocalOffset,
                    transform.RenderMatrix,
                    Solid,
                    Color);
        }
    }
}