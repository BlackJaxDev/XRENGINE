using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    public partial class DebugDrawComponent
    {
        public class DebugDrawLine(Vector3 start, Vector3 end, ColorF4 color) : DebugShapeBase(color, false)
        {
            /// <summary>
            /// Parameterless constructor for serialization.
            /// </summary>
            public DebugDrawLine() : this(Vector3.Zero, Vector3.Zero, ColorF4.White) { }

            /// <summary>
            /// Start position relative to this component's transform.
            /// </summary>
            public Vector3 StartOffset
            {
                get => start;
                set => SetField(ref start, value);
            }

            /// <summary>
            /// End position relative to this component's transform.
            /// </summary>
            public Vector3 EndOffset
            {
                get => end;
                set => SetField(ref end, value);
            }

            public override void Render(TransformBase transform)
                => Engine.Rendering.Debug.RenderLine(
                    transform.TransformPoint(StartOffset, true),
                    transform.TransformPoint(EndOffset, true),
                    Color);
        }
    }
}