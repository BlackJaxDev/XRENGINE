using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public interface IGLTexture : IGLObject
    {
        ETextureTarget TextureTarget { get; }
        bool IsInvalidated { get; }
        void Bind();
        void Clear(ColorF4 color, int level = 0);
        void Invalidate();
        void PostSampling();
        void PreSampling();
        void PushData();
        string ResolveSamplerName(int textureIndex, string? samplerNameOverride);
        Vector3 WidthHeightDepth { get; }
    }
}