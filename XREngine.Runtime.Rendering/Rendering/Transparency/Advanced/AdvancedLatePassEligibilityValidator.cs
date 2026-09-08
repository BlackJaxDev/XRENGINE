using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Enforces late-pass eligibility rules and rejects legacy forward opaque bypasses.
/// </summary>
public static class AdvancedLatePassEligibilityValidator
{
    /// <summary>
    /// Validates whether a given draw can execute in the requested late pass.
    /// Opaque and masked materials are strictly forbidden from entering late passes.
    /// </summary>
    public static bool TryValidateLatePass(
        AdvancedLatePassMetadata metadata,
        bool isOpaqueOrMasked,
        out string? rejectionReason)
    {
        if (isOpaqueOrMasked)
        {
            rejectionReason = "Opaque and masked surfaces must be classified and shaded natively through ARP 06/07 and cannot enter late transparency passes.";
            return false;
        }

        if (metadata.UnsupportedReason != null)
        {
            rejectionReason = metadata.UnsupportedReason;
            return false;
        }

        rejectionReason = null;
        return true;
    }

    /// <summary>
    /// Validates the authored material lane against the concrete executable
    /// render pass. Advanced late transparency requires explicit metadata so a
    /// pass assignment alone cannot bypass native opaque classification.
    /// </summary>
    public static bool TryValidateLatePass(
        XRMaterial? material,
        int renderPass,
        out string? rejectionReason)
    {
        if (!TryGetExpectedKindMask(renderPass, out uint expectedKinds))
        {
            rejectionReason = null;
            return true;
        }

        if (material is null)
        {
            rejectionReason = "The late-pass mesh has no material.";
            return false;
        }

        ETransparencyMode mode = material.GetEffectiveTransparencyMode();
        bool isOpaqueOrMasked =
            GpuTransparencyClassification.ResolveDomain(mode) is
                EGpuTransparencyDomain.OpaqueOrOther or
                EGpuTransparencyDomain.Masked;
        AdvancedLatePassMetadata? metadata = material.AdvancedLatePassMetadata;
        if (metadata is null)
        {
            // On-top is an intentional overlay lane and may contain opaque
            // editor/debug geometry. Its render-pass assignment is already the
            // executable authoring contract; metadata remains optional there.
            if (renderPass == (int)EDefaultRenderPass.OnTopForward)
            {
                rejectionReason = null;
                return true;
            }
            rejectionReason = isOpaqueOrMasked
                ? "Opaque and masked surfaces must use native Advanced classification and shading."
                : "The material has no explicit Advanced late-pass eligibility metadata.";
            return false;
        }

        bool rejectOpaqueOrMasked =
            isOpaqueOrMasked &&
            renderPass != (int)EDefaultRenderPass.OnTopForward;
        if (!TryValidateLatePass(metadata, rejectOpaqueOrMasked, out rejectionReason))
            return false;

        if ((uint)metadata.Kind > (uint)EAdvancedLatePassKind.UserInterface)
        {
            rejectionReason = "The material declares an unknown Advanced late-pass kind.";
            return false;
        }
        uint kindBit = 1u << (int)metadata.Kind;
        if ((expectedKinds & kindBit) == 0u)
        {
            rejectionReason = "The material's Advanced late-pass kind does not match the executable render pass.";
            return false;
        }

        return true;
    }

    public static bool IsAdvancedLateRenderPass(int renderPass)
        => TryGetExpectedKindMask(renderPass, out _);

    private static bool TryGetExpectedKindMask(
        int renderPass,
        out uint expectedKinds)
    {
        expectedKinds = renderPass switch
        {
            (int)EDefaultRenderPass.TransparentForward =>
                KindBit(EAdvancedLatePassKind.SortedAlpha) |
                KindBit(EAdvancedLatePassKind.ParticipatingTransparency) |
                KindBit(EAdvancedLatePassKind.Refraction) |
                KindBit(EAdvancedLatePassKind.SpecialEffects),
            (int)EDefaultRenderPass.WeightedBlendedOitForward =>
                KindBit(EAdvancedLatePassKind.WeightedBlendedOit),
            (int)EDefaultRenderPass.PerPixelLinkedListForward =>
                KindBit(EAdvancedLatePassKind.Ppll),
            (int)EDefaultRenderPass.DepthPeelingForward =>
                KindBit(EAdvancedLatePassKind.DepthPeeling),
            (int)EDefaultRenderPass.OnTopForward =>
                KindBit(EAdvancedLatePassKind.OnTopOverlay) |
                KindBit(EAdvancedLatePassKind.SpecialEffects),
            _ => 0u,
        };
        return expectedKinds != 0u;
    }

    private static uint KindBit(EAdvancedLatePassKind kind)
        => 1u << checked((int)kind);

}
