namespace XREngine.Rendering;

/// <summary>
/// Status and readiness criteria for the Advanced Render Pipeline production cutover.
/// Certifies that ARP 01 through ARP 09 are complete and classic G-Buffer stages are eliminated.
/// </summary>
public static class AdvancedProductionCutoverContract
{
    public const string ProductionPipelineName = "AdvancedRenderPipeline";
    public const string ProductionOpenXrPipelineName = "RvcRenderPipeline";

    /// <summary>
    /// Evaluates whether all required architectural milestones have passed for full cutover.
    /// </summary>
    public static bool EvaluateCutoverReadiness(
        bool hasClassification,
        bool hasNativeShading,
        bool hasTransparency,
        bool hasStereoMultiview,
        bool isClassicGBufferEliminated,
        bool isOpenXrEyeOwnershipPreserved,
        out string? blockerReason)
    {
        if (!hasClassification)
        {
            blockerReason = "ARP 06 GPU Material Classification is not active or verified.";
            return false;
        }

        if (!hasNativeShading)
        {
            blockerReason = "ARP 07 Native Opaque Shading & Clustered Lighting is not active or verified.";
            return false;
        }

        if (!hasTransparency)
        {
            blockerReason = "ARP 08 Transparency, Special Passes, & Post Chain is not active or verified.";
            return false;
        }

        if (!hasStereoMultiview)
        {
            blockerReason = "ARP 09 Stereo, Multiview, & Editor View Integration is not active or verified.";
            return false;
        }

        if (!isClassicGBufferEliminated)
        {
            blockerReason = "Classic multi-channel G-Buffer passes must be completely eliminated from the production path.";
            return false;
        }

        if (!isOpenXrEyeOwnershipPreserved)
        {
            blockerReason = "Production OpenXR eye ownership must remain strictly preserved in RvcRenderPipeline.";
            return false;
        }

        blockerReason = null;
        return true;
    }
    /// <summary>
    /// Evaluates one concrete output profile. Admission only proves that the backend accepted
    /// the executable stage family; it never supplies image, runtime, or production evidence.
    /// </summary>
    public static AdvancedProductionCutoverStatus EvaluateStatus(
        AdvancedRenderPipeline pipeline,
        in AdvancedVisibilityFamilyAdmission admission,
        EAdvancedRenderPipelineOutputBindingState bindingState,
        bool reservationCurrent)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        string? providerBlocker = GetProviderBlocker(pipeline);
        if (providerBlocker is not null)
            return new(EAdvancedProductionExecutionState.Unsupported,
                EAdvancedRuntimeValidationState.NotApplicable,
                EAdvancedProductionAcceptanceState.NotApplicable, providerBlocker);

        if (admission.State == EAdvancedProductionExecutionState.PendingResources ||
            bindingState == EAdvancedRenderPipelineOutputBindingState.PendingResources)
        {
            return new(EAdvancedProductionExecutionState.PendingResources,
                EAdvancedRuntimeValidationState.NotApplicable,
                EAdvancedProductionAcceptanceState.NotApplicable, admission.Reason);
        }

        if (admission.State != EAdvancedProductionExecutionState.Admitted ||
            bindingState != EAdvancedRenderPipelineOutputBindingState.Bound ||
            !reservationCurrent)
        {
            string blocker = admission.State == EAdvancedProductionExecutionState.Admitted
                ? "The Advanced output reservation is not current for this renderer generation."
                : admission.Reason;
            return new(EAdvancedProductionExecutionState.Unsupported,
                EAdvancedRuntimeValidationState.NotApplicable,
                EAdvancedProductionAcceptanceState.NotApplicable, blocker);
        }

        return new(EAdvancedProductionExecutionState.Admitted,
            EAdvancedRuntimeValidationState.Pending,
            EAdvancedProductionAcceptanceState.Pending,
            "Advanced execution is admitted, but no durable runtime/output validation or production acceptance evidence is recorded for this profile.");
    }

    /// <summary>Evaluates a pipeline asset before it is realized for a physical output.</summary>
    public static AdvancedProductionCutoverStatus EvaluateUnboundProfile(
        AdvancedRenderPipeline pipeline,
        in AdvancedRenderPipelineCapabilityResult capabilities)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        string? providerBlocker = GetProviderBlocker(pipeline);
        if (providerBlocker is not null)
            return new(EAdvancedProductionExecutionState.Unsupported,
                EAdvancedRuntimeValidationState.NotApplicable,
                EAdvancedProductionAcceptanceState.NotApplicable, providerBlocker);
        if (!capabilities.IsSupported)
            return new(EAdvancedProductionExecutionState.Unsupported,
                EAdvancedRuntimeValidationState.NotApplicable,
                EAdvancedProductionAcceptanceState.NotApplicable, capabilities.Diagnostic);
        return new(EAdvancedProductionExecutionState.PendingResources,
            EAdvancedRuntimeValidationState.NotApplicable,
            EAdvancedProductionAcceptanceState.NotApplicable,
            "Advanced output admission has not been evaluated for this physical output.");
    }

    private static string? GetProviderBlocker(AdvancedRenderPipeline pipeline)
    {
        IAdvancedGlobalIlluminationProvider? gi = pipeline.GlobalIlluminationProvider;
        if (pipeline.GlobalIlluminationMode != EGlobalIlluminationMode.LightProbesAndIbl)
        {
            if (gi is null)
                return $"Global illumination mode '{pipeline.GlobalIlluminationMode}' is requested but no Advanced GI provider is configured.";
            if (!gi.IsSupported)
                return $"Advanced GI provider '{gi.ProviderName}' is unsupported.";
            if (gi.ActiveMode != pipeline.GlobalIlluminationMode)
                return $"Advanced GI provider '{gi.ProviderName}' does not implement requested mode '{pipeline.GlobalIlluminationMode}'.";
            return $"Advanced GI provider '{gi.ProviderName}' is configured but is not integrated into native shading.";
        }

        IAdvancedAmbientOcclusionProvider? ao = pipeline.AmbientOcclusionProvider;
        if (ao is null)
            return null;
        if (!ao.IsSupported)
            return $"Advanced AO provider '{ao.ProviderName}' is unsupported.";
        return $"Advanced AO provider '{ao.ProviderName}' is configured but is not integrated into native shading.";
    }
}
