using System.Numerics;

namespace XREngine.Browser;

/// <summary>A portable physical canvas rectangle and its cached camera projection.</summary>
public readonly record struct BrowserViewport(int X, int Y, int Width, int Height, Matrix4x4 ViewProjection,
    Matrix4x4 View)
{
    public static BrowserViewport Create(int x, int y, int width, int height, Matrix4x4 view)
    {
        if (width <= 0 || height <= 0)
            return new BrowserViewport(x, y, width, height, Matrix4x4.Identity, view);

        float aspect = (float)width / height;
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3.0f, aspect, 0.1f, 100.0f);
        return new BrowserViewport(x, y, width, height, view * projection, view);
    }
}
