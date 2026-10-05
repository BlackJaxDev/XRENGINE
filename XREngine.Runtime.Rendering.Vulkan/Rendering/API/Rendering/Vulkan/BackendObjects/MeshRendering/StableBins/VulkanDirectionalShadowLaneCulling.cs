using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Derives, per sealed bin record, the cascades a caster must be drawn into.
/// It applies the same conservative world-AABB-versus-clip-volume test as the
/// generic layered shadow pass so the lane draws the same caster set into each
/// cascade tile.
/// </summary>
internal static class VulkanDirectionalShadowLaneCulling
{
    /// <summary>
    /// Writes one mask per record: bit <c>i</c> set when the record casts
    /// shadows and its candidate bounds intersect cascade <c>i</c>. A record
    /// without a resolvable candidate keeps every cascade so culling can never
    /// remove a legitimate caster.
    /// </summary>
    internal static void ComputeRecordMasks(
        ReadOnlySpan<VulkanPreparedStableBinRecord> records,
        ReadOnlySpan<AdvancedVisibilityCandidate> candidates,
        ReadOnlySpan<Matrix4x4> viewProjections,
        Span<byte> masks)
    {
        if (masks.Length < records.Length)
            throw new ArgumentException("The record mask span is smaller than the record span.", nameof(masks));

        int cascadeCount = Math.Min(viewProjections.Length, 8);
        byte allCascades = (byte)((1 << cascadeCount) - 1);
        bool depthZeroToOne =
            RuntimeEngine.Rendering.EffectiveClipDepthRange == ERenderClipDepthRange.ZeroToOne;
        for (int recordIndex = 0; recordIndex < records.Length; recordIndex++)
        {
            ref readonly VulkanPreparedStableBinRecord record = ref records[recordIndex];
            if ((record.VisibilityDrawFlags & (uint)GPUIndirectRenderFlags.CastShadow) == 0u)
            {
                masks[recordIndex] = 0;
                continue;
            }

            int payloadIndex = record.VisibilityPayloadIndex;
            if ((uint)payloadIndex >= (uint)candidates.Length)
            {
                masks[recordIndex] = allCascades;
                continue;
            }

            ref readonly AdvancedVisibilityCandidate candidate = ref candidates[payloadIndex];
            AABB bounds = new(
                new Vector3(candidate.BoundsMin.X, candidate.BoundsMin.Y, candidate.BoundsMin.Z),
                new Vector3(candidate.BoundsMax.X, candidate.BoundsMax.Y, candidate.BoundsMax.Z));
            if (!bounds.IsValid)
            {
                masks[recordIndex] = allCascades;
                continue;
            }

            byte mask = 0;
            for (int cascade = 0; cascade < cascadeCount; cascade++)
            {
                if (LayeredShadowUniformState.IntersectsHomogeneousClipVolume(
                        in bounds,
                        in viewProjections[cascade],
                        depthZeroToOne))
                {
                    mask |= (byte)(1 << cascade);
                }
            }

            masks[recordIndex] = mask;
        }
    }
}
