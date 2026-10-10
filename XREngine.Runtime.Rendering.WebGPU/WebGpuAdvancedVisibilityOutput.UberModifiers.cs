using System.Numerics;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuAdvancedVisibilityOutput
{
    private static string? UberModifierRejection(in AdvancedVisibilityStageBackendRequest request,
        AdvancedGpuScenePublicationSnapshot snapshot, in AdvancedDrawRecord draw)
    {
        if (request.ShadingDebugView is EAdvancedShadingDebugView.DirectDiffuse or EAdvancedShadingDebugView.DirectSpecular or
            EAdvancedShadingDebugView.ShadowMask or EAdvancedShadingDebugView.ShadowFallbackReason)
            return "WebGPU.Advanced.UberLightingDebugUnsupported: this Uber receiver exports exact composed source lighting; the selected decomposed-light or shadow diagnostic requires its own source outputs.";
        if (!snapshot.Instances.TryGet(draw.Instance, out AdvancedInstanceRecord instance))
            return "WebGPU.Advanced.UberReceiverMissing: modifier admission requires the exact retained receiver instance.";
        ReadOnlySpan<AdvancedDecalRecord> decals = snapshot.GlobalResources.Decals.PhysicalRecords;
        for (int index = 0; index < decals.Length; index++)
        {
            ref readonly AdvancedDecalRecord decal = ref decals[index];
            if (!snapshot.GlobalResources.Decals.TryGetDenseIndex(decal.Identity, out uint dense) || dense != (uint)index ||
                (decal.Flags & AdvancedDecalRecord.EnabledFlag) == 0 ||
                (decal.Flags & AdvancedDecalRecord.UnsupportedAuthoredFlag) != 0 ||
                (decal.ViewMaskLo | decal.ViewMaskHi) != 0 && (decal.ViewMaskLo & (1u << (int)request.NativeViewIndex)) == 0 ||
                decal.LayerMask != 0 && (decal.LayerMask & instance.LayerMask) == 0 ||
                decal.HalfExtentsAndFade.X <= 0 || decal.HalfExtentsAndFade.Y <= 0 || decal.HalfExtentsAndFade.Z <= 0)
                continue;
            if ((decal.Flags & AdvancedDecalRecord.AuthoredAlbedoFlag) != 0)
            {
                if (!request.EnableAuthoredDecals || instance.Reserved0 != 1 || request.BackendReadyPackage is not { } package ||
                    package.AuthoredDecalCommandKeys.IndexOf(decal.AuthoredCommandKey) < 0) continue;
            }
            if (MayIntersectDecal(in instance, in decal))
                return "WebGPU.Advanced.UberDecalReceiverUnsupported: an enabled selected decal targets this Uber receiver's view/layer and its conservative world bounds overlap the decal box; source-exact composed lighting requires a separately qualified decal receiver.";
        }
        return null;
    }

    private static bool MayIntersectDecal(in AdvancedInstanceRecord receiver, in AdvancedDecalRecord decal)
    {
        Matrix4x4 matrix = decal.WorldToDecal;
        if (matrix.M14 != 0 || matrix.M24 != 0 || matrix.M34 != 0 || matrix.M44 != 1) return true;
        Vector3 low = new(float.PositiveInfinity), high = new(float.NegativeInfinity);
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 world = new((corner & 1) == 0 ? receiver.BoundsMin.X : receiver.BoundsMax.X,
                (corner & 2) == 0 ? receiver.BoundsMin.Y : receiver.BoundsMax.Y,
                (corner & 4) == 0 ? receiver.BoundsMin.Z : receiver.BoundsMax.Z);
            Vector3 local = Vector3.Transform(world, matrix);
            if (!float.IsFinite(local.X) || !float.IsFinite(local.Y) || !float.IsFinite(local.Z)) return true;
            low = Vector3.Min(low, local); high = Vector3.Max(high, local);
        }
        Vector3 half = new(decal.HalfExtentsAndFade.X, decal.HalfExtentsAndFade.Y, decal.HalfExtentsAndFade.Z);
        return low.X <= half.X && high.X >= -half.X && low.Y <= half.Y && high.Y >= -half.Y && low.Z <= half.Z && high.Z >= -half.Z;
    }
}
