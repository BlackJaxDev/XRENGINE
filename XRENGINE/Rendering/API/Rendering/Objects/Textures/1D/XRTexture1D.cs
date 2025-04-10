using Silk.NET.OpenGL.Extensions.OVR;
using System.Numerics;

namespace XREngine.Rendering
{
    public class XRTexture1D : XRTexture
    {
        public override uint MaxDimension { get; } = 1u;
        public float Width { get; set; } = 1f;
        override public Vector3 WidthHeightDepth => new(Width, 1, 1);
    }
}