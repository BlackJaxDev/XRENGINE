using System;
using System.Numerics;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Computes double-precision clip-space reprojection matrices for temporal
/// anti-aliasing so deep non-reversed depth buffers do not fail validation
/// from single-precision matrix inversion rounding.
/// </summary>
internal static class TemporalMatrixMath
{
    /// <summary>
    /// Computes the clip-space reprojection matrix that maps a current jittered
    /// clip position into the previous frame's jittered clip space.
    /// </summary>
    internal static bool TryComputeCurrentToPreviousClip(
        in Matrix4x4 currViewProjection,
        in Matrix4x4 prevViewProjection,
        in Matrix4x4 currViewProjectionUnjittered,
        in Matrix4x4 prevViewProjectionUnjittered,
        Vector2 currentJitterUv,
        Vector2 previousJitterUv,
        out Matrix4x4 currentToPreviousClip)
    {
        if (currViewProjectionUnjittered == prevViewProjectionUnjittered)
        {
            // When the unjittered view-projection is unchanged, the exact clip
            // transform is a pure translation by the jitter delta in NDC.
            Matrix4x4 exact = Matrix4x4.Identity;
            exact.M41 = (previousJitterUv.X - currentJitterUv.X) * 2.0f;
            exact.M42 = (previousJitterUv.Y - currentJitterUv.Y) * 2.0f;
            currentToPreviousClip = exact;
            return VPRC_TemporalAccumulationPass.IsTemporalMatrixFinite(currentToPreviousClip);
        }

        Span<double> curr = stackalloc double[16];
        Span<double> invCurr = stackalloc double[16];
        Span<double> prev = stackalloc double[16];
        Span<double> product = stackalloc double[16];

        ReadRowMajor(in currViewProjection, curr);
        if (!TryInvert4x4(curr, invCurr))
        {
            currentToPreviousClip = Matrix4x4.Identity;
            return false;
        }

        ReadRowMajor(in prevViewProjection, prev);
        Multiply4x4(invCurr, prev, product);
        currentToPreviousClip = WriteRowMajor(product);
        return VPRC_TemporalAccumulationPass.IsTemporalMatrixFinite(currentToPreviousClip);
    }

    /// <summary>
    /// Inverts a 4x4 matrix in double precision to preserve mantissa bits for
    /// ill-conditioned perspective projections before casting back to float.
    /// </summary>
    internal static bool TryInvertDouble(in Matrix4x4 matrix, out Matrix4x4 inverse)
    {
        Span<double> src = stackalloc double[16];
        Span<double> dst = stackalloc double[16];
        ReadRowMajor(in matrix, src);
        if (!TryInvert4x4(src, dst))
        {
            inverse = Matrix4x4.Identity;
            return false;
        }

        inverse = WriteRowMajor(dst);
        return VPRC_TemporalAccumulationPass.IsTemporalMatrixFinite(inverse);
    }

    private static void ReadRowMajor(in Matrix4x4 m, Span<double> dst)
    {
        dst[0] = m.M11; dst[1] = m.M12; dst[2] = m.M13; dst[3] = m.M14;
        dst[4] = m.M21; dst[5] = m.M22; dst[6] = m.M23; dst[7] = m.M24;
        dst[8] = m.M31; dst[9] = m.M32; dst[10] = m.M33; dst[11] = m.M34;
        dst[12] = m.M41; dst[13] = m.M42; dst[14] = m.M43; dst[15] = m.M44;
    }

    private static Matrix4x4 WriteRowMajor(ReadOnlySpan<double> src)
        => new(
            (float)src[0], (float)src[1], (float)src[2], (float)src[3],
            (float)src[4], (float)src[5], (float)src[6], (float)src[7],
            (float)src[8], (float)src[9], (float)src[10], (float)src[11],
            (float)src[12], (float)src[13], (float)src[14], (float)src[15]);

    private static void Multiply4x4(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> dst)
    {
        for (int row = 0; row < 4; row++)
        {
            int rowBase = row * 4;
            for (int col = 0; col < 4; col++)
            {
                dst[rowBase + col] =
                    a[rowBase + 0] * b[0 + col] +
                    a[rowBase + 1] * b[4 + col] +
                    a[rowBase + 2] * b[8 + col] +
                    a[rowBase + 3] * b[12 + col];
            }
        }
    }

    private static bool TryInvert4x4(ReadOnlySpan<double> src, Span<double> dst)
    {
        Span<double> aug = stackalloc double[32];
        for (int row = 0; row < 4; row++)
        {
            int srcBase = row * 4;
            int augBase = row * 8;
            for (int col = 0; col < 4; col++)
            {
                aug[augBase + col] = src[srcBase + col];
                aug[augBase + 4 + col] = row == col ? 1.0 : 0.0;
            }
        }

        for (int pivotCol = 0; pivotCol < 4; pivotCol++)
        {
            int pivotRow = pivotCol;
            double bestAbs = Math.Abs(aug[pivotCol * 8 + pivotCol]);
            for (int row = pivotCol + 1; row < 4; row++)
            {
                double candidate = Math.Abs(aug[row * 8 + pivotCol]);
                if (candidate > bestAbs)
                {
                    bestAbs = candidate;
                    pivotRow = row;
                }
            }

            if (bestAbs <= 1e-18 || !double.IsFinite(bestAbs))
                return false;

            if (pivotRow != pivotCol)
            {
                int rowA = pivotCol * 8;
                int rowB = pivotRow * 8;
                for (int col = 0; col < 8; col++)
                    (aug[rowA + col], aug[rowB + col]) = (aug[rowB + col], aug[rowA + col]);
            }

            int pivotBase = pivotCol * 8;
            double invPivot = 1.0 / aug[pivotBase + pivotCol];
            for (int col = 0; col < 8; col++)
                aug[pivotBase + col] *= invPivot;

            for (int row = 0; row < 4; row++)
            {
                if (row == pivotCol)
                    continue;

                int rowBase = row * 8;
                double factor = aug[rowBase + pivotCol];
                if (factor == 0.0)
                    continue;

                for (int col = 0; col < 8; col++)
                    aug[rowBase + col] -= factor * aug[pivotBase + col];
            }
        }

        for (int row = 0; row < 4; row++)
        {
            int dstBase = row * 4;
            int augBase = row * 8 + 4;
            for (int col = 0; col < 4; col++)
            {
                double value = aug[augBase + col];
                if (!double.IsFinite(value))
                    return false;
                dst[dstBase + col] = value;
            }
        }

        return true;
    }
}
