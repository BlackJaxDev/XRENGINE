using System;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

public static class GpuSortPolicy
{
    public static EGpuSortDomain ResolveSortDomain(int renderPass, EGpuSortDomainPolicy policy)
    {
        return policy switch
        {
            EGpuSortDomainPolicy.MaterialStateGrouping => EGpuSortDomain.MaterialStateGrouping,
            EGpuSortDomainPolicy.OpaqueFrontToBackAndMaterial =>
                IsOpaquePass(renderPass)
                    ? EGpuSortDomain.OpaqueFrontToBack
                    : EGpuSortDomain.MaterialStateGrouping,
            EGpuSortDomainPolicy.OpaqueFrontToBackTransparentBackToFront => ResolveFullDomain(renderPass),
            _ => EGpuSortDomain.MaterialStateGrouping,
        };
    }

    public static GPUSortDirection ResolveSortDirection(EGpuSortDomain domain)
        => domain == EGpuSortDomain.TransparentBackToFront
            ? GPUSortDirection.Descending
            : GPUSortDirection.Ascending;

    public static bool UsesDistance(EGpuSortDomain domain)
        => domain == EGpuSortDomain.OpaqueFrontToBack ||
           domain == EGpuSortDomain.TransparentBackToFront;

    public static uint EncodeDistanceSortKey(float renderDistance, GPUSortDirection direction)
    {
        uint raw = BitConverter.SingleToUInt32Bits(MathF.Max(0.0f, renderDistance));
        return direction == GPUSortDirection.Descending ? ~raw : raw;
    }

    private static EGpuSortDomain ResolveFullDomain(int renderPass)
    {
        if (IsTransparentPass(renderPass))
            return EGpuSortDomain.TransparentBackToFront;

        if (IsOpaquePass(renderPass))
            return EGpuSortDomain.OpaqueFrontToBack;

        return EGpuSortDomain.MaterialStateGrouping;
    }

    private static bool IsOpaquePass(int renderPass)
        => renderPass == (int)EDefaultRenderPass.OpaqueDeferred ||
           renderPass == (int)EDefaultRenderPass.OpaqueForward;

    private static bool IsTransparentPass(int renderPass)
        => renderPass == (int)EDefaultRenderPass.DeferredDecals ||
           renderPass == (int)EDefaultRenderPass.TransparentForward;
}