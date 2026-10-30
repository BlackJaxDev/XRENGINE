using System.Numerics;

namespace XREngine.Rendering.Models.Gaussian.Baking.Lod;

/// <summary>Finds the eigenvalues and eigenvectors of a symmetric 3x3 matrix.</summary>
public static class SymmetricEigen3
{
    /// <summary>The largest number of Jacobi sweeps.</summary>
    private const int MaxSweeps = 32;

    /// <summary>The solver stops when the off-diagonal norm is less than this value × the diagonal norm.</summary>
    private const double RelativeTolerance = 1e-15;

    /// <summary>
    /// Decomposes a symmetric matrix. The values are sorted from large to small.
    /// The columns of the returned rotation are the eigenvectors (local +X, +Y, +Z). They form a right-handed basis.
    /// </summary>
    public static (Vector3 Values, Quaternion Rotation) Decompose(float xx, float xy, float xz, float yy, float yz, float zz)
    {
        double[,] a =
        {
            { xx, xy, xz },
            { xy, yy, yz },
            { xz, yz, zz },
        };
        double[,] v =
        {
            { 1, 0, 0 },
            { 0, 1, 0 },
            { 0, 0, 1 },
        };

        for (int sweep = 0; sweep < MaxSweeps; sweep++)
        {
            double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
            double diag = a[0, 0] * a[0, 0] + a[1, 1] * a[1, 1] + a[2, 2] * a[2, 2];
            if (off == 0.0 || off <= RelativeTolerance * RelativeTolerance * diag)
                break;

            Rotate(a, v, 0, 1);
            Rotate(a, v, 0, 2);
            Rotate(a, v, 1, 2);
        }

        int[] order = [0, 1, 2];
        double[] values = [a[0, 0], a[1, 1], a[2, 2]];
        // Insertion sort by descending value. The sort is stable.
        for (int i = 1; i < 3; i++)
        {
            int key = order[i];
            int j = i - 1;
            while (j >= 0 && values[order[j]] < values[key])
            {
                order[j + 1] = order[j];
                j--;
            }
            order[j + 1] = key;
        }

        Vector3 e0 = Column(v, order[0]);
        Vector3 e1 = Column(v, order[1]);
        Vector3 e2 = Column(v, order[2]);
        if (Vector3.Dot(Vector3.Cross(e0, e1), e2) < 0.0f)
            e2 = -e2;

        // System.Numerics rotation matrices keep the rotated axes in the rows.
        Matrix4x4 m = new(
            e0.X, e0.Y, e0.Z, 0,
            e1.X, e1.Y, e1.Z, 0,
            e2.X, e2.Y, e2.Z, 0,
            0, 0, 0, 1);
        Quaternion q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
        return (new Vector3((float)values[order[0]], (float)values[order[1]], (float)values[order[2]]), q);
    }

    private static Vector3 Column(double[,] v, int column)
        => Vector3.Normalize(new Vector3((float)v[0, column], (float)v[1, column], (float)v[2, column]));

    private static void Rotate(double[,] a, double[,] v, int p, int q)
    {
        double apq = a[p, q];
        if (apq == 0.0)
            return;

        double theta = (a[q, q] - a[p, p]) / (2.0 * apq);
        double sign = theta < 0.0 ? -1.0 : 1.0;
        double t = sign / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
        double c = 1.0 / Math.Sqrt(t * t + 1.0);
        double s = t * c;

        for (int k = 0; k < 3; k++)
        {
            double akp = a[k, p];
            double akq = a[k, q];
            a[k, p] = c * akp - s * akq;
            a[k, q] = s * akp + c * akq;
        }
        for (int k = 0; k < 3; k++)
        {
            double apk = a[p, k];
            double aqk = a[q, k];
            a[p, k] = c * apk - s * aqk;
            a[q, k] = s * apk + c * aqk;
        }
        for (int k = 0; k < 3; k++)
        {
            double vkp = v[k, p];
            double vkq = v[k, q];
            v[k, p] = c * vkp - s * vkq;
            v[k, q] = s * vkp + c * vkq;
        }
    }
}
