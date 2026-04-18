using System.Numerics;

namespace XREngine.Extensions
{
    public static class ColorExtensions
    {
        public static Vector3 ToVector3(this System.Drawing.Color color) =>
            new(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f);

        public static Vector4 ToVector4(this System.Drawing.Color color) =>
            new(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, color.A / 255.0f);
    }
}
