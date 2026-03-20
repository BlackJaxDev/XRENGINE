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
        Vector4 quaternionWxyz = new(1.0f, 0.0f, 0.0f, 0.0f);

        for (int i = 0; i < 8; i++)
        {
            Vector4 next = new(
                (sxx + syy + szz) * quaternionWxyz.X + (syz - szy) * quaternionWxyz.Y + (szx - sxz) * quaternionWxyz.Z + (sxy - syx) * quaternionWxyz.W,
                (syz - szy) * quaternionWxyz.X + (sxx - syy - szz) * quaternionWxyz.Y + (sxy + syx) * quaternionWxyz.Z + (szx + sxz) * quaternionWxyz.W,
                (szx - sxz) * quaternionWxyz.X + (sxy + syx) * quaternionWxyz.Y + (-sxx + syy - szz) * quaternionWxyz.Z + (syz + szy) * quaternionWxyz.W,
                (sxy - syx) * quaternionWxyz.X + (szx + sxz) * quaternionWxyz.Y + (syz + szy) * quaternionWxyz.Z + (-sxx - syy + szz) * quaternionWxyz.W);

            float lengthSquared = next.LengthSquared();
            if (lengthSquared <= MinWeight)
                return Quaternion.Identity;

            quaternionWxyz = next / MathF.Sqrt(lengthSquared);
        }

        if (quaternionWxyz.X < 0.0f)
            quaternionWxyz = -quaternionWxyz;

        Quaternion rotation = new(quaternionWxyz.Y, quaternionWxyz.Z, quaternionWxyz.W, quaternionWxyz.X);
        return rotation.LengthSquared() > MinWeight ? Quaternion.Normalize(rotation) : Quaternion.Identity;
    }
}