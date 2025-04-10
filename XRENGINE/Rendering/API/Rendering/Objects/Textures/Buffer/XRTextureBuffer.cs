using Assimp;
using System.Numerics;

namespace XREngine.Rendering
{
    public class XRTextureBuffer : XRTexture
    {
        public override uint MaxDimension { get; }
        public override Vector3 WidthHeightDepth => new(0, 0, 0);
    }
}