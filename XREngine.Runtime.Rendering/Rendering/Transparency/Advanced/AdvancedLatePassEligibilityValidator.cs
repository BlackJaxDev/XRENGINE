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
        out string? rejectionReason,
        bool supportsSourceTriangleCoverage = false)
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

        if (!supportsSourceTriangleCoverage && NativeUberMaterialSourceEncoder.RequiresPrimitiveTileDiscard(material))
        {
            rejectionReason = "Vertex-mode UDIM discard requires canonical triangle coverage and is not supported in authored late passes.";
            return false;
        }

        ETransparencyMode mode = material.GetEffectiveTransparencyMode();
        bool isOpaqueOrMasked =
            GpuTransparencyClassification.ResolveDomain(mode) is
                EGpuTransparencyDomain.OpaqueOrOther or
                EGpuTransparencyDomain.Masked;
        AdvancedLatePassMetadata? metadata = material.ResolveAdvancedLatePassMetadata();
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

        bool verifiedSourceOrdered = metadata.SourceSurfaceContract != EAdvancedSourceSurfaceContract.None &&
            IsVerifiedSourceOrderedBase(material, renderPass, metadata);
        if (metadata.SourceSurfaceContract != EAdvancedSourceSurfaceContract.None &&
            !verifiedSourceOrdered)
        {
            rejectionReason = "The source-ordered surface does not match its verified Uber base-pass contract.";
            return false;
        }

        // UI materials keep the opaque mode that batch lowering requires, but they blend through explicit raster
        // state and draw in painter order. The UserInterface kind admits them to the late lanes.
        bool rejectOpaqueOrMasked =
            isOpaqueOrMasked &&
            renderPass != (int)EDefaultRenderPass.OnTopForward &&
            metadata.Kind != EAdvancedLatePassKind.UserInterface &&
            !IsVerifiedSoftOutlineBase(material, renderPass, metadata) &&
            !verifiedSourceOrdered;
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

    internal static bool IsVerifiedSoftOutlineBase(
        XRMaterial material,
        int renderPass,
        AdvancedLatePassMetadata metadata)
    {
        if (renderPass != (int)EDefaultRenderPass.TransparentForward ||
            material.EngineSemantic != EngineMaterialSemanticIdentity.NativeUberV1 ||
            material.GetEffectiveTransparencyMode() != ETransparencyMode.Opaque ||
            metadata.Kind != EAdvancedLatePassKind.SpecialEffects ||
            material.PassSet.SourceRenderQueue != 3000 ||
            material.PassSet.QueuePriority != 0 ||
            !material.PassSet.TryGetPass(EMaterialPassIdentity.Base, out MaterialPassDefinition basePass) ||
            !basePass.Enabled || basePass.RenderPass != renderPass ||
            !ReferenceEquals(basePass.RenderOptions, material.RenderOptions) ||
            !material.PassSet.TryGetPass(EMaterialPassIdentity.Outline, out MaterialPassDefinition outlinePass) ||
            !outlinePass.Enabled || outlinePass.RenderPass != renderPass ||
            outlinePass.ShaderBehavior != EngineMaterialSemanticIdentity.UberSoftOutlineV2)
            return false;

        RenderingParameters options = basePass.RenderOptions;
        return options.DepthTest.Enabled == ERenderParamUsage.Enabled &&
               options.DepthTest.UpdateDepth &&
               options.BlendModeAllDrawBuffers is { Enabled: ERenderParamUsage.Disabled };
    }

    /// <summary>Checks the imported source queue and canonical Uber base draw before admitting it after native shading.</summary>
    internal static bool IsVerifiedSourceOrderedBase(
        XRMaterial material,
        int renderPass,
        AdvancedLatePassMetadata metadata)
    {
        if (renderPass != (int)EDefaultRenderPass.TransparentForward ||
            material.EngineSemantic != EngineMaterialSemanticIdentity.NativeUberV1 ||
            metadata.SourceSurfaceContract is not
                (EAdvancedSourceSurfaceContract.OrderedToonV1 or EAdvancedSourceSurfaceContract.SceneColorToonV1) ||
            metadata.Kind != EAdvancedLatePassKind.SpecialEffects ||
            !metadata.IsOrderDependent ||
            material.PassSet.SourceRenderQueue <= 2500 ||
            material.PassSet.QueuePriority != material.PassSet.SourceRenderQueue - 3000 ||
            (material.PassSet.TryGetPass(EMaterialPassIdentity.EarlyDepth, out MaterialPassDefinition earlyDepthPass) &&
                earlyDepthPass.Enabled) ||
            (material.PassSet.TryGetPass(EMaterialPassIdentity.Outline, out MaterialPassDefinition outlinePass) &&
                outlinePass.Enabled) ||
            !material.PassSet.TryGetPass(EMaterialPassIdentity.Base, out MaterialPassDefinition basePass) ||
            !basePass.Enabled || basePass.RenderPass != renderPass ||
            !ReferenceEquals(basePass.RenderOptions, material.RenderOptions) ||
            basePass.RenderOptions.AlphaToCoverage != ERenderParamUsage.Disabled ||
            !material.HasCurrentVerifiedUberSourceIdentity())
            return false;

        return metadata.SourceSurfaceContract switch
        {
            EAdvancedSourceSurfaceContract.OrderedToonV1 =>
                !metadata.RequiresSceneColorSnapshot,
            EAdvancedSourceSurfaceContract.SceneColorToonV1 =>
                metadata.RequiresSceneColorSnapshot &&
                metadata.SceneColorSamplerName == AdvancedSceneColorContract.SourceOrderedSamplerName &&
                metadata.SceneColorTextureUnit >= 0,
            _ => false,
        };
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
                KindBit(EAdvancedLatePassKind.SpecialEffects) |
                KindBit(EAdvancedLatePassKind.UserInterface),
            (int)EDefaultRenderPass.PostBloomForward =>
                KindBit(EAdvancedLatePassKind.SortedAlpha) |
                KindBit(EAdvancedLatePassKind.OnTopOverlay) |
                KindBit(EAdvancedLatePassKind.SpecialEffects),
            (int)EDefaultRenderPass.PostMotionBlurForward =>
                KindBit(EAdvancedLatePassKind.SortedAlpha) |
                KindBit(EAdvancedLatePassKind.SpecialEffects),
            (int)EDefaultRenderPass.PostDepthOfFieldForward =>
                KindBit(EAdvancedLatePassKind.SortedAlpha) |
                KindBit(EAdvancedLatePassKind.SpecialEffects),
            (int)EDefaultRenderPass.WeightedBlendedOitForward =>
                KindBit(EAdvancedLatePassKind.WeightedBlendedOit),
            (int)EDefaultRenderPass.PerPixelLinkedListForward =>
                KindBit(EAdvancedLatePassKind.Ppll),
            (int)EDefaultRenderPass.DepthPeelingForward =>
                KindBit(EAdvancedLatePassKind.DepthPeeling),
            (int)EDefaultRenderPass.OnTopForward =>
                KindBit(EAdvancedLatePassKind.OnTopOverlay) |
                KindBit(EAdvancedLatePassKind.SpecialEffects) |
                KindBit(EAdvancedLatePassKind.UserInterface),
            _ => 0u,
        };
        return expectedKinds != 0u;
    }

    private static uint KindBit(EAdvancedLatePassKind kind)
        => 1u << checked((int)kind);

}
