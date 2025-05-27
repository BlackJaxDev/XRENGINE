using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    public partial class DebugDrawComponent
    {
        public abstract class DebugShapeBase(ColorF4 color, bool solid) : XRBase
        {
            /// <summary>
            /// The color of the shape.
            /// </summary>
            public ColorF4 Color
            {
                get => color;
                set => SetField(ref color, value);
            }

            /// <summary>
            /// If true, the shape will be solid and filled in.
            /// If false, it will be wireframe.
            /// </summary>
            public bool Solid 
            {
                get => solid;
                set => SetField(ref solid, value);
            }

            public abstract void Render(TransformBase transform);
        }
    }
}