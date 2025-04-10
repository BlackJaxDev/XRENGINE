using System.Numerics;

namespace XREngine.Rendering
{
    public class XRTextureCubeArray : XRTexture
    {
        public override uint MaxDimension { get; } = 2;
        public override Vector3 WidthHeightDepth =>  new(0, 0, 0);
    }
}