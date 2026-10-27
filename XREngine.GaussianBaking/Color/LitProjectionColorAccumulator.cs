using System.Numerics;
using XREngine.Rendering.Models.Gaussian.Baking.Sampling;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;
using XREngine.Rendering.Models.Gaussian.Baking.Views;

namespace XREngine.Rendering.Models.Gaussian.Baking.Color;

/// <summary>
/// Collects the color of each leaf splat from lit images. A leaf takes the weighted average of the pixels of the
/// views that can see it. Call <see cref="Accumulate"/> for each batch of views, and then call <see cref="Finish"/> once.
/// </summary>
/// <remarks>
/// <para>The camera looks along its local −Z axis, and its local +Y axis is up, as in <see cref="BakeCameraPose"/>.
/// The first row of an image is the top row. A pixel center is at (x + 0.5, y + 0.5).</para>
/// <para>A view sees a leaf when the leaf is in the frame, the leaf faces the camera
/// (<c>dot(normal, direction to camera) &gt; 0.1</c>), and no triangle of the rest surface is between them.
/// The weight of a view is the cosine of the facing angle.</para>
/// </remarks>
public sealed class LitProjectionColorAccumulator
{
    /// <summary>A leaf must face the camera with a cosine larger than this value.</summary>
    private const float MinimumFacing = 0.1f;

    /// <summary>The ray tolerance, as a fraction of the diagonal of the bounds of the rest surface.</summary>
    private const float OcclusionEpsilonFraction = 1e-3f;

    private readonly BakedSplat[] _leaves;
    private readonly int _width;
    private readonly int _height;
    private readonly TriangleBvh _bvh;
    private readonly float _epsilon;
    private readonly double[] _sums;
    private readonly double[] _weights;
    private bool _finished;

    /// <summary>Creates an accumulator.</summary>
    /// <param name="leaves">The leaf splats. <see cref="Finish"/> writes their colors in place.</param>
    /// <param name="rest">The surface at the rest pose. It hides leaves from the views.</param>
    /// <param name="width">The width of each image, in pixels.</param>
    /// <param name="height">The height of each image, in pixels.</param>
    public LitProjectionColorAccumulator(BakedSplat[] leaves, DeformedSurface rest, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(leaves);
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        _leaves = leaves;
        _width = width;
        _height = height;
        _bvh = new TriangleBvh(rest);
        _epsilon = OcclusionEpsilonFraction * BoundsDiagonal(rest);
        _sums = new double[leaves.Length * 3];
        _weights = new double[leaves.Length];
    }

    /// <summary>Gets the number of leaves that at least one view saw. The value is final after <see cref="Finish"/>.</summary>
    public int SeenLeafCount { get; private set; }

    /// <summary>Adds the samples of a batch of views.</summary>
    /// <param name="views">The cameras of the images.</param>
    /// <param name="images">One linear RGBA image for each view, with width × height × 4 values and the top row first.</param>
    public void Accumulate(IReadOnlyList<BakeCameraPose> views, float[][] images)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(images);
        if (_finished)
            throw new InvalidOperationException("The accumulator is finished.");
        if (views.Count != images.Length)
            throw new ArgumentException("The number of images must match the number of views.", nameof(images));

        int expectedLength = _width * _height * 4;
        for (int v = 0; v < views.Count; v++)
        {
            if (images[v] is null || images[v].Length != expectedLength)
                throw new ArgumentException($"Image {v} must have {expectedLength} values.", nameof(images));

            AccumulateView(views[v], images[v]);
        }
    }

    /// <summary>
    /// Fills the leaves that no view saw from the nearest seen leaf, writes the colors, and scales them.
    /// </summary>
    /// <returns>
    /// The color scale: the larger of 1 and the 99th percentile of the luminance. Every color is divided by it.
    /// The value is 1 when no view saw any leaf.
    /// </returns>
    public float Finish()
    {
        if (_finished)
            throw new InvalidOperationException("The accumulator is finished.");
        _finished = true;

        int count = _leaves.Length;
        Vector3[] colors = new Vector3[count];
        bool[] seen = new bool[count];
        int seenCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (_weights[i] <= 0.0)
                continue;

            seen[i] = true;
            seenCount++;
            colors[i] = new Vector3(
                (float)(_sums[3 * i] / _weights[i]),
                (float)(_sums[3 * i + 1] / _weights[i]),
                (float)(_sums[3 * i + 2] / _weights[i]));
        }

        SeenLeafCount = seenCount;
        if (seenCount == 0)
            return 1.0f;

        Vector3[] filled = new Vector3[count];
        for (int i = 0; i < count; i++)
            filled[i] = seen[i] ? colors[i] : colors[NearestSeen(seen, i)];

        float scale = ComputeScale(filled);
        for (int i = 0; i < count; i++)
            _leaves[i].LinearColor = filled[i] / scale;

        return scale;
    }

    private void AccumulateView(BakeCameraPose view, float[] image)
    {
        Quaternion inverseRotation = Quaternion.Inverse(view.Rotation);
        float tanHalfFov = MathF.Tan(view.VerticalFovDegrees * 0.5f * MathF.PI / 180.0f);
        float aspect = _width / (float)_height;

        for (int i = 0; i < _leaves.Length; i++)
        {
            ref readonly BakedSplat leaf = ref _leaves[i];
            Vector3 toCamera = view.Position - leaf.Position;
            float distance = toCamera.Length();
            if (!(distance > 0.0f))
                continue;

            Vector3 toCameraDirection = toCamera / distance;
            float facing = Vector3.Dot(leaf.Normal, toCameraDirection);
            if (!(facing > MinimumFacing))
                continue;

            Vector3 local = Vector3.Transform(leaf.Position - view.Position, inverseRotation);
            float depth = -local.Z;
            if (!(depth > 0.0f))
                continue;

            float ndcX = local.X / (depth * tanHalfFov * aspect);
            float ndcY = local.Y / (depth * tanHalfFov);
            if (!(MathF.Abs(ndcX) <= 1.0f && MathF.Abs(ndcY) <= 1.0f))
                continue;

            float maxDistance = distance - _epsilon;
            if (maxDistance > 0.0f && _bvh.Raycast(view.Position, -toCameraDirection, maxDistance, out _))
                continue;

            float pixelX = (ndcX * 0.5f + 0.5f) * _width;
            float pixelY = (0.5f - ndcY * 0.5f) * _height;
            Vector3 color = SampleBilinear(image, pixelX, pixelY);
            _sums[3 * i] += facing * color.X;
            _sums[3 * i + 1] += facing * color.Y;
            _sums[3 * i + 2] += facing * color.Z;
            _weights[i] += facing;
        }
    }

    private Vector3 SampleBilinear(float[] image, float pixelX, float pixelY)
    {
        float fx = pixelX - 0.5f;
        float fy = pixelY - 0.5f;
        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        float tx = fx - x0;
        float ty = fy - y0;
        int x1 = Math.Clamp(x0 + 1, 0, _width - 1);
        int y1 = Math.Clamp(y0 + 1, 0, _height - 1);
        x0 = Math.Clamp(x0, 0, _width - 1);
        y0 = Math.Clamp(y0, 0, _height - 1);

        Vector3 top = Vector3.Lerp(Pixel(image, x0, y0), Pixel(image, x1, y0), tx);
        Vector3 bottom = Vector3.Lerp(Pixel(image, x0, y1), Pixel(image, x1, y1), tx);
        return Vector3.Lerp(top, bottom, ty);
    }

    private Vector3 Pixel(float[] image, int x, int y)
    {
        int offset = (y * _width + x) * 4;
        return new Vector3(image[offset], image[offset + 1], image[offset + 2]);
    }

    /// <summary>Finds the seen leaf nearest in array index, searching i−1, i+1, i−2, i+2, and so on.</summary>
    private static int NearestSeen(bool[] seen, int index)
    {
        for (int offset = 1; offset < seen.Length; offset++)
        {
            int below = index - offset;
            if (below >= 0 && seen[below])
                return below;

            int above = index + offset;
            if (above < seen.Length && seen[above])
                return above;
        }

        return index;
    }

    private static float ComputeScale(Vector3[] colors)
    {
        double[] luminances = new double[colors.Length];
        for (int i = 0; i < colors.Length; i++)
            luminances[i] = 0.2126 * colors[i].X + 0.7152 * colors[i].Y + 0.0722 * colors[i].Z;

        Array.Sort(luminances);
        int rank = (int)Math.Ceiling(0.99 * luminances.Length);
        double percentile = luminances[Math.Clamp(rank - 1, 0, luminances.Length - 1)];
        return (float)Math.Max(1.0, percentile);
    }

    private static float BoundsDiagonal(DeformedSurface surface)
    {
        float[] positions = surface.Positions;
        int vertexCount = Math.Min(surface.VertexCount, positions.Length / 3);
        if (vertexCount == 0)
            return 0.0f;

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        for (int i = 0; i < vertexCount; i++)
        {
            Vector3 point = new(positions[3 * i], positions[3 * i + 1], positions[3 * i + 2]);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        return (max - min).Length();
    }
}
