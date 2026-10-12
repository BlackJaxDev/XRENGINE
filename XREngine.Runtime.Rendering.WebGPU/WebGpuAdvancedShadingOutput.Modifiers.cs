using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuAdvancedShadingOutput
{
    private static bool HasCurrentAuthoredDecalCount(in AdvancedVisibilityStageBackendRequest request,
        WebGpuAdvancedShadingFrame frame)
        => frame.AuthoredDecalCount == (request.EnableAuthoredDecals && request.BackendReadyPackage is { } package
            ? package.AuthoredDecalCommandKeys.Length : 0);

    private static bool AreNativeDecalsAbsent(AdvancedGpuScenePublicationSnapshot snapshot,
        in AdvancedVisibilityStageBackendRequest request, WebGpuAdvancedShadingFrame frame)
    {
        if (frame.AuthoredDecalCount != 0 ||
            request.BackendReadyPackage is not { State: EBackendReadyFramePackageState.Published } package ||
            snapshot.GlobalResources.Sequence == 0 ||
            snapshot.GlobalResources.Sequence != snapshot.MaterialPayloads.Sequence ||
            request.EnableAuthoredDecals && !package.AuthoredDecalCommandKeys.IsEmpty)
            return false;

        // Generic decals do not follow the authored DeferredDecals switch. Keep
        // every enabled candidate, including stale identities, on the full path.
        foreach (ref readonly AdvancedDecalRecord decal in snapshot.GlobalResources.Decals.PhysicalRecords)
            if ((decal.Flags & AdvancedDecalRecord.EnabledFlag) != 0 &&
                (decal.Flags & (AdvancedDecalRecord.AuthoredAlbedoFlag | AdvancedDecalRecord.UnsupportedAuthoredFlag)) == 0 &&
                ViewMatches(decal.ViewMaskLo, decal.ViewMaskHi, request.NativeViewIndex))
                return false;
        return true;
    }

    private static bool AreNativeModifiersAbsent(AdvancedGpuScenePublicationSnapshot snapshot,
        in AdvancedVisibilityStageBackendRequest request, WebGpuAdvancedShadingFrame frame)
    {
        if (!frame.NativeDecalsAbsent || frame.DepthComparisonBank) return false;
        // Inspect the complete physical light range consumed by native shading.
        // A missing or stale selected shadow is a diagnostic, not an absence.
        const EAdvancedLightRecordFlags shadowFlags = EAdvancedLightRecordFlags.Enabled | EAdvancedLightRecordFlags.CastsShadow;
        foreach (ref readonly AdvancedLightRecord light in snapshot.GlobalResources.Lights.PhysicalRecords)
            if ((light.Flags & shadowFlags) == shadowFlags &&
                ViewMatches(light.ViewMaskLo, light.ViewMaskHi, request.NativeViewIndex))
                return false;
        return true;
    }

    private static bool HasModifierAbsentPrograms(XRRenderPipelineInstance instance,
        in AdvancedVisibilityStageBackendRequest request, WebGpuAdvancedShadingFrame frame)
    {
        if (!frame.NativeModifiersAbsent || frame.DepthComparisonBank || !HasCurrentAuthoredDecalCount(in request, frame))
            return false;
        bool multisample = request.MsaaSampleCount == 4;
        string native = multisample ? "advanced::shade-native-no-modifiers-msaa" : "advanced::shade-native-no-modifiers";
        string exports = multisample ? "advanced::shade-surface-exports-no-modifiers-msaa" : "advanced::shade-surface-exports-no-modifiers";
        return instance.Pipeline!.TryGetWebPipelineArtifact(native, out _) &&
            (!request.RequiresMaterialSurfaceExports || instance.Pipeline.TryGetWebPipelineArtifact(exports, out _));
    }

    private static bool HasDecalAbsentPrograms(XRRenderPipelineInstance instance,
        in AdvancedVisibilityStageBackendRequest request, WebGpuAdvancedShadingFrame frame)
    {
        if (!frame.NativeDecalsAbsent || !HasCurrentAuthoredDecalCount(in request, frame)) return false;
        bool multisample = request.MsaaSampleCount == 4;
        string native = multisample
            ? frame.DepthComparisonBank ? "advanced::shade-native-depth-no-decals-msaa" : "advanced::shade-native-no-decals-msaa"
            : frame.DepthComparisonBank ? "advanced::shade-native-depth-no-decals" : "advanced::shade-native-no-decals";
        string exports = multisample
            ? frame.DepthComparisonBank ? "advanced::shade-surface-exports-depth-no-decals-msaa" : "advanced::shade-surface-exports-no-decals-msaa"
            : frame.DepthComparisonBank ? "advanced::shade-surface-exports-depth-no-decals" : "advanced::shade-surface-exports-no-decals";
        return instance.Pipeline!.TryGetWebPipelineArtifact(native, out _) &&
            (!request.RequiresMaterialSurfaceExports || instance.Pipeline.TryGetWebPipelineArtifact(exports, out _));
    }

    private static EWebGpuAdvancedNativeShadingFamily SelectNativeFamily(XRRenderPipelineInstance instance,
        in AdvancedVisibilityStageBackendRequest request, WebGpuAdvancedShadingFrame frame)
    {
        if (frame.SelectedNativeFamily != EWebGpuAdvancedNativeShadingFamily.Unselected &&
            (frame.SelectedNativeFamilySampleCount != request.MsaaSampleCount ||
             frame.SelectedNativeFamilyRequiresExports != request.RequiresMaterialSurfaceExports))
        {
            if (frame.SelectedNativeFamilyFrameSequence == frame.ClassifiedSequence &&
                frame.SelectedNativeFamilyPreparationGeneration == frame.PreparationGeneration)
                throw Invalid("NativeFamilyRequirementsChanged",
                    "the selected native shading family requires the original sample and surface-export settings");
            frame.SelectedNativeFamily = EWebGpuAdvancedNativeShadingFamily.Unselected;
        }
        if (frame.SelectedNativeFamily == EWebGpuAdvancedNativeShadingFamily.Unselected)
        {
            frame.SelectedNativeFamily = HasModifierAbsentPrograms(instance, in request, frame)
                ? EWebGpuAdvancedNativeShadingFamily.ModifiersAbsent
                : HasDecalAbsentPrograms(instance, in request, frame)
                    ? EWebGpuAdvancedNativeShadingFamily.DecalsAbsent : EWebGpuAdvancedNativeShadingFamily.Full;
            frame.SelectedNativeFamilySampleCount = request.MsaaSampleCount;
            frame.SelectedNativeFamilyRequiresExports = request.RequiresMaterialSurfaceExports;
        }
        frame.SelectedNativeFamilyFrameSequence = frame.ClassifiedSequence;
        frame.SelectedNativeFamilyPreparationGeneration = frame.PreparationGeneration;
        return frame.SelectedNativeFamily;
    }

    private static string NativePass(WebGpuAdvancedShadingFrame frame,
        EWebGpuAdvancedNativeShadingFamily family, bool multisample, bool exports)
        => (family, frame.DepthComparisonBank, multisample, exports) switch
        {
            (EWebGpuAdvancedNativeShadingFamily.ModifiersAbsent, _, false, false) => "shade-native-no-modifiers",
            (EWebGpuAdvancedNativeShadingFamily.ModifiersAbsent, _, false, true) => "shade-surface-exports-no-modifiers",
            (EWebGpuAdvancedNativeShadingFamily.ModifiersAbsent, _, true, false) => "shade-native-no-modifiers-msaa",
            (EWebGpuAdvancedNativeShadingFamily.ModifiersAbsent, _, true, true) => "shade-surface-exports-no-modifiers-msaa",
            (EWebGpuAdvancedNativeShadingFamily.DecalsAbsent, false, false, false) => "shade-native-no-decals",
            (EWebGpuAdvancedNativeShadingFamily.DecalsAbsent, true, false, false) => "shade-native-depth-no-decals",
            (EWebGpuAdvancedNativeShadingFamily.DecalsAbsent, false, false, true) => "shade-surface-exports-no-decals",
            (EWebGpuAdvancedNativeShadingFamily.DecalsAbsent, true, false, true) => "shade-surface-exports-depth-no-decals",
            (EWebGpuAdvancedNativeShadingFamily.DecalsAbsent, false, true, false) => "shade-native-no-decals-msaa",
            (EWebGpuAdvancedNativeShadingFamily.DecalsAbsent, true, true, false) => "shade-native-depth-no-decals-msaa",
            (EWebGpuAdvancedNativeShadingFamily.DecalsAbsent, false, true, true) => "shade-surface-exports-no-decals-msaa",
            (EWebGpuAdvancedNativeShadingFamily.DecalsAbsent, true, true, true) => "shade-surface-exports-depth-no-decals-msaa",
            (EWebGpuAdvancedNativeShadingFamily.Full, false, false, false) => "shade-native",
            (EWebGpuAdvancedNativeShadingFamily.Full, true, false, false) => "shade-native-depth",
            (EWebGpuAdvancedNativeShadingFamily.Full, false, false, true) => "shade-surface-exports",
            (EWebGpuAdvancedNativeShadingFamily.Full, true, false, true) => "shade-surface-exports-depth",
            (EWebGpuAdvancedNativeShadingFamily.Full, false, true, false) => "shade-native-msaa",
            (EWebGpuAdvancedNativeShadingFamily.Full, true, true, false) => "shade-native-depth-msaa",
            (EWebGpuAdvancedNativeShadingFamily.Full, false, true, true) => "shade-surface-exports-msaa",
            (EWebGpuAdvancedNativeShadingFamily.Full, true, true, true) => "shade-surface-exports-depth-msaa",
            _ => throw Invalid("NativeFamilyMissing", "the selected native shading family has no matching program"),
        };
}
