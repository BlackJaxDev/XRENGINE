using System.Collections.Generic;
using System.Numerics;

namespace XREngine.Rendering.Compute;

internal static class GPUSoftbodyClusterMath
{
    private const float MinWeight = 0.000001f;

    public static bool TrySolveClusterTransform(
        IReadOnlyList<GPUSoftbodyParticleData> particles,
        IReadOnlyList<GPUSoftbodyClusterMemberData> clusterMembers,
        in GPUSoftbodyClusterData cluster,
        out Vector3 center,
        out Quaternion rotation)
    {
        center = cluster.RestCenter;
        rotation = Quaternion.Identity;

        if (cluster.MemberCount <= 0 || cluster.MemberStart < 0 || cluster.MemberStart + cluster.MemberCount > clusterMembers.Count)
            return false;

        float totalWeight = 0.0f;
        Vector3 weightedCenter = Vector3.Zero;
        for (int i = 0; i < cluster.MemberCount; i++)
        {
            GPUSoftbodyClusterMemberData member = clusterMembers[cluster.MemberStart + i];
            if (member.ParticleIndex < 0 || member.ParticleIndex >= particles.Count)
                continue;

            float weight = MathF.Max(member.Weight, 0.0f);
            if (weight <= 0.0f)
                continue;

            weightedCenter += particles[member.ParticleIndex].CurrentPosition * weight;
            totalWeight += weight;
        }

        if (totalWeight <= MinWeight)
            return false;

        center = weightedCenter / totalWeight;

        float sxx = 0.0f;
        float sxy = 0.0f;
        float sxz = 0.0f;
        float syx = 0.0f;
        float syy = 0.0f;
        float syz = 0.0f;
        float szx = 0.0f;
        float szy = 0.0f;
        float szz = 0.0f;

        for (int i = 0; i < cluster.MemberCount; i++)
        {
            GPUSoftbodyClusterMemberData member = clusterMembers[cluster.MemberStart + i];
            if (member.ParticleIndex < 0 || member.ParticleIndex >= particles.Count)
                continue;

            float weight = MathF.Max(member.Weight, 0.0f);
            if (weight <= 0.0f)
                continue;

            Vector3 currentOffset = particles[member.ParticleIndex].CurrentPosition - center;
            Vector3 restOffset = member.LocalOffset;

            sxx += weight * currentOffset.X * restOffset.X;
            sxy += weight * currentOffset.X * restOffset.Y;
            sxz += weight * currentOffset.X * restOffset.Z;
            syx += weight * currentOffset.Y * restOffset.X;
            syy += weight * currentOffset.Y * restOffset.Y;
            syz += weight * currentOffset.Y * restOffset.Z;
            szx += weight * currentOffset.Z * restOffset.X;
            szy += weight * currentOffset.Z * restOffset.Y;
            szz += weight * currentOffset.Z * restOffset.Z;
        }

        rotation = SolveHornQuaternion(sxx, sxy, sxz, syx, syy, syz, szx, szy, szz);
        return true;
    }

    private static Quaternion SolveHornQuaternion(
        float sxx,
        float sxy,
        float sxz,
        float syx,
        float syy,
        float syz,
        float szx,
        float szy,
        float szz)
    {
        // Match Finalize.comp: current * rest^T covariance requires these signs,
        // and the best fit is the largest algebraic eigenvalue, not magnitude.
        Span<float> a = stackalloc float[16]
        {
            sxx + syy + szz, szy - syz, sxz - szx, syx - sxy,
            szy - syz, sxx - syy - szz, sxy + syx, szx + sxz,
            sxz - szx, sxy + syx, -sxx + syy - szz, syz + szy,
            syx - sxy, szx + sxz, syz + szy, -sxx - syy + szz,
        };
        float scale = 0.0f;
        for (int i = 0; i < a.Length; i++)
        {
            if (!float.IsFinite(a[i]))
                return Quaternion.Identity;
            scale = MathF.Max(scale, MathF.Abs(a[i]));
        }
        if (scale == 0.0f)
            return Quaternion.Identity;
        for (int i = 0; i < a.Length; i++)
            a[i] /= scale;
        Span<float> eigenvectors = stackalloc float[16]
        {
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        };
        for (int sweep = 0; sweep < 6; sweep++)
        {
            bool changed = false;
            for (int p = 0; p < 3; p++)
            {
                for (int q = p + 1; q < 4; q++)
                {
                    float offDiagonal = a[p * 4 + q];
                    if (MathF.Abs(offDiagonal) <= MinWeight)
                        continue;
                    changed = true;
                    float tau = (a[q * 4 + q] - a[p * 4 + p]) / (2.0f * offDiagonal);
                    float t = (tau >= 0.0f ? 1.0f : -1.0f) / (MathF.Abs(tau) + MathF.Sqrt(1.0f + tau * tau));
                    float c = 1.0f / MathF.Sqrt(1.0f + t * t);
                    float s = t * c;
                    a[p * 4 + p] -= t * offDiagonal;
                    a[q * 4 + q] += t * offDiagonal;
                    a[p * 4 + q] = a[q * 4 + p] = 0.0f;
                    for (int k = 0; k < 4; k++)
                    {
                        if (k != p && k != q)
                        {
                            float kp = a[k * 4 + p];
                            float kq = a[k * 4 + q];
                            a[k * 4 + p] = a[p * 4 + k] = c * kp - s * kq;
                            a[k * 4 + q] = a[q * 4 + k] = s * kp + c * kq;
                        }
                        float vp = eigenvectors[k * 4 + p];
                        float vq = eigenvectors[k * 4 + q];
                        eigenvectors[k * 4 + p] = c * vp - s * vq;
                        eigenvectors[k * 4 + q] = s * vp + c * vq;
                    }
                }
            }
            if (!changed)
                break;
        }
        int best = 0;
        for (int i = 1; i < 4; i++)
            if (a[i * 4 + i] > a[best * 4 + best] + MinWeight ||
                (MathF.Abs(a[i * 4 + i] - a[best * 4 + best]) <= MinWeight && MathF.Abs(eigenvectors[i]) > MathF.Abs(eigenvectors[best])))
                best = i;
        Quaternion rotation = Quaternion.Normalize(new(
            eigenvectors[4 + best], eigenvectors[8 + best], eigenvectors[12 + best], eigenvectors[best]));
        return rotation.W < 0.0f ? -rotation : rotation;
    }
}
