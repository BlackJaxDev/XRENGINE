using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>
/// Maps points inside a bounding box onto an integer grid with 2^bits - 1 steps on each axis.
/// An axis with zero extent quantizes to 0 and dequantizes to the minimum of the box.
/// </summary>
public readonly struct GridQuantizer
{
    /// <summary>The smallest number of grid bits that <see cref="ChooseGridBits"/> returns.</summary>
    public const byte MinGridBits = 8;

    /// <summary>The largest number of grid bits. Grid values must fit in 16 bits.</summary>
    public const byte MaxGridBits = 16;

    private readonly Vector3 _min;
    private readonly Vector3 _step;
    private readonly Vector3 _inverseStep;
    private readonly float _maxCell;

    /// <param name="bounds">The box that the grid covers.</param>
    /// <param name="gridBits">The number of bits for each axis, from 1 to 16.</param>
    public GridQuantizer(AABB bounds, byte gridBits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gridBits, (byte)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(gridBits, MaxGridBits);

        float maxCell = (1 << gridBits) - 1;
        Vector3 extent = bounds.Max - bounds.Min;
        _min = bounds.Min;
        _maxCell = maxCell;
        _step = new Vector3(Step(extent.X, maxCell), Step(extent.Y, maxCell), Step(extent.Z, maxCell));
        _inverseStep = new Vector3(Inverse(_step.X), Inverse(_step.Y), Inverse(_step.Z));
        MaxErrorMeters = MathF.Max(_step.X, MathF.Max(_step.Y, _step.Z)) * 0.5f;
    }

    /// <summary>The largest error on one axis after a round trip, which is half of the largest step.</summary>
    public float MaxErrorMeters { get; }

    /// <summary>The point of grid cell (0, 0, 0): the minimum of the box.</summary>
    public Vector3 Origin => _min;

    /// <summary>The size of one grid cell on each axis. An axis with zero extent has a cell size of 0.</summary>
    public Vector3 CellSize => _step;

    /// <summary>Rounds a point to the nearest grid node. Points outside the box clamp to the border.</summary>
    public void Quantize(Vector3 p, out ushort x, out ushort y, out ushort z)
    {
        x = QuantizeAxis(p.X - _min.X, _inverseStep.X);
        y = QuantizeAxis(p.Y - _min.Y, _inverseStep.Y);
        z = QuantizeAxis(p.Z - _min.Z, _inverseStep.Z);
    }

    /// <summary>
    /// Returns the grid node of the grid values: <c>Origin + (x, y, z) × CellSize</c>. A grid node is a point of the
    /// lattice, not the center of a cell. <see cref="Quantize"/> rounds a point to the nearest grid node, so the round
    /// trip error on an axis is at most half of <see cref="CellSize"/> (<see cref="MaxErrorMeters"/>).
    /// </summary>
    public Vector3 Dequantize(ushort x, ushort y, ushort z)
        => _min + new Vector3(x * _step.X, y * _step.Y, z * _step.Z);

    /// <summary>
    /// Returns the smallest number of bits from 8 to 16 for which half of the largest box extent divided by
    /// (2^bits - 1) is not more than <paramref name="maxErrorMeters"/>. Returns 16 when no number qualifies.
    /// </summary>
    public static byte ChooseGridBits(AABB bounds, float maxErrorMeters)
    {
        Vector3 extent = bounds.Max - bounds.Min;
        float maxExtent = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
        for (byte bits = MinGridBits; bits < MaxGridBits; bits++)
        {
            float error = maxExtent / ((1 << bits) - 1) * 0.5f;
            if (error <= maxErrorMeters)
                return bits;
        }
        return MaxGridBits;
    }

    private static float Step(float extent, float maxCell)
        => extent > 0f ? extent / maxCell : 0f;

    private static float Inverse(float step)
        => step > 0f ? 1f / step : 0f;

    private ushort QuantizeAxis(float offset, float inverseStep)
    {
        float cell = MathF.Round(offset * inverseStep, MidpointRounding.AwayFromZero);
        if (!(cell > 0f))
            return 0;
        if (cell > _maxCell)
            cell = _maxCell;
        return (ushort)cell;
    }
}
