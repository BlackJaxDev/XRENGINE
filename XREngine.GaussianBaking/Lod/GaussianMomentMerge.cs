using System.Numerics;
using XREngine.Rendering.Models.Gaussian.Baking.Sampling;

namespace XREngine.Rendering.Models.Gaussian.Baking.Lod;

/// <summary>Merges a group of splats into one parent splat by moment matching.</summary>
public static class GaussianMomentMerge
{
    /// <summary>The smallest scale of a merged splat, in meters. It keeps the volume above zero.</summary>
    private const float MinSigma = 1e-9f;

    /// <summary>The weighted normal is not usable when its length is less than this value.</summary>
    private const double MinNormalLength = 1e-12;

    /// <summary>Makes the parent of the child splats. The children must not be empty.</summary>
    public static BakedSplat Merge(ReadOnlySpan<BakedSplat> children)
    {
        if (children.IsEmpty)
            throw new ArgumentException("At least one child splat is necessary.", nameof(children));

        int count = children.Length;
        Span<double> weights = count <= 64 ? stackalloc double[count] : new double[count];
        double sumW = 0.0;
        double sumAlphaVolume = 0.0;
        for (int i = 0; i < count; i++)
        {
            ref readonly BakedSplat c = ref children[i];
            double volume = (double)c.Scale.X * c.Scale.Y * c.Scale.Z;
            double alphaVolume = c.Opacity * volume;
            weights[i] = alphaVolume;
            sumW += alphaVolume;
            sumAlphaVolume += alphaVolume;
        }
        if (!(sumW > 0.0))
        {
            // No child has weight. Use equal weights.
            for (int i = 0; i < count; i++)
                weights[i] = 1.0;
            sumW = count;
        }

        double mx = 0, my = 0, mz = 0, cr = 0, cg = 0, cb = 0;
        for (int i = 0; i < count; i++)
        {
            ref readonly BakedSplat c = ref children[i];
            double w = weights[i];
            mx += w * c.Position.X;
            my += w * c.Position.Y;
            mz += w * c.Position.Z;
            cr += w * c.LinearColor.X;
            cg += w * c.LinearColor.Y;
            cb += w * c.LinearColor.Z;
        }
        mx /= sumW;
        my /= sumW;
        mz /= sumW;

        double sxx = 0, sxy = 0, sxz = 0, syy = 0, syz = 0, szz = 0;
        double nx = 0, ny = 0, nz = 0;
        bool allStatic = true;
        for (int i = 0; i < count; i++)
        {
            ref readonly BakedSplat c = ref children[i];
            double w = weights[i];
            Vector3 ax = Vector3.Transform(Vector3.UnitX, c.Rotation);
            Vector3 ay = Vector3.Transform(Vector3.UnitY, c.Rotation);
            Vector3 az = Vector3.Transform(Vector3.UnitZ, c.Rotation);
            double vx = (double)c.Scale.X * c.Scale.X;
            double vy = (double)c.Scale.Y * c.Scale.Y;
            double vz = (double)c.Scale.Z * c.Scale.Z;
            double dx = c.Position.X - mx;
            double dy = c.Position.Y - my;
            double dz = c.Position.Z - mz;

            sxx += w * (vx * ax.X * ax.X + vy * ay.X * ay.X + vz * az.X * az.X + dx * dx);
            sxy += w * (vx * ax.X * ax.Y + vy * ay.X * ay.Y + vz * az.X * az.Y + dx * dy);
            sxz += w * (vx * ax.X * ax.Z + vy * ay.X * ay.Z + vz * az.X * az.Z + dx * dz);
            syy += w * (vx * ax.Y * ax.Y + vy * ay.Y * ay.Y + vz * az.Y * az.Y + dy * dy);
            syz += w * (vx * ax.Y * ax.Z + vy * ay.Y * ay.Z + vz * az.Y * az.Z + dy * dz);
            szz += w * (vx * ax.Z * ax.Z + vy * ay.Z * ay.Z + vz * az.Z * az.Z + dz * dz);

            nx += w * c.Normal.X;
            ny += w * c.Normal.Y;
            nz += w * c.Normal.Z;
            allStatic &= c.IsStatic;
        }

        (Vector3 values, Quaternion rotation) = SymmetricEigen3.Decompose(
            (float)(sxx / sumW), (float)(sxy / sumW), (float)(sxz / sumW),
            (float)(syy / sumW), (float)(syz / sumW), (float)(szz / sumW));

        Vector3 sigma = new(
            MathF.Max(MathF.Sqrt(MathF.Max(values.X, 0.0f)), MinSigma),
            MathF.Max(MathF.Sqrt(MathF.Max(values.Y, 0.0f)), MinSigma),
            MathF.Max(MathF.Sqrt(MathF.Max(values.Z, 0.0f)), MinSigma));

        double parentVolume = (double)sigma.X * sigma.Y * sigma.Z;
        float opacity = (float)Math.Clamp(sumAlphaVolume / parentVolume, 0.0, 1.0);

        Vector3 normal;
        double normalLength = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (normalLength > MinNormalLength)
            normal = new Vector3((float)(nx / normalLength), (float)(ny / normalLength), (float)(nz / normalLength));
        else
            normal = Vector3.Transform(Vector3.UnitZ, rotation);

        return new BakedSplat
        {
            Position = new Vector3((float)mx, (float)my, (float)mz),
            Rotation = rotation,
            Scale = sigma,
            LinearColor = new Vector3((float)(cr / sumW), (float)(cg / sumW), (float)(cb / sumW)),
            Opacity = opacity,
            Normal = normal,
            SourceTriangle = -1,
            IsStatic = allStatic,
        };
    }
}
