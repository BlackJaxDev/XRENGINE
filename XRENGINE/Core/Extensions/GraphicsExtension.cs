using System.Drawing;

namespace XREngine.Extensions
{
    public static class GraphicsExtensions
    {
        public static void RotateTransformAt(this Graphics g, float angle, PointF point)
        {
            g.TranslateTransform(point.X, point.Y);
            g.RotateTransform(angle);
            g.TranslateTransform(-point.X, -point.Y);
        }
        public static void ScaleTransformAt(this Graphics g, float xScale, float yScale, PointF point)
        {
            g.TranslateTransform(point.X, point.Y);
            g.ScaleTransform(xScale, yScale);
            g.TranslateTransform(-point.X, -point.Y);
        }
    }
}
