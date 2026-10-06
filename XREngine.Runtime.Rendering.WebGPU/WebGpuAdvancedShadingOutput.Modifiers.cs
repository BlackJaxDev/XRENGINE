using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuAdvancedShadingOutput
{
    private static bool HasCurrentAuthoredDecalCount(in AdvancedVisibilityStageBackendRequest request,
        WebGpuAdvancedShadingFrame frame)
        => frame.AuthoredDecalCount == (request.EnableAuthoredDecals && request.BackendReadyPackage is { } package
            ? package.AuthoredDecalCommandKeys.Length : 0);

    private static bool AreNativeModifiersAbsent(AdvancedGpuScenePublicationSnapshot snapshot,
        in AdvancedVisibilityStageBackendRequest request, WebGpuAdvancedShadingFrame frame)
    {
        if (frame.DepthComparisonBank || frame.AuthoredDecalCount != 0 ||
            request.BackendReadyPackage is not { State: EBackendReadyFramePackageState.Published } package ||
            snapshot.GlobalResources.Sequence == 0 ||
            snapshot.GlobalResources.Sequence != snapshot.MaterialPayloads.Sequence ||
            request.EnableAuthoredDecals && !package.AuthoredDecalCommandKeys.IsEmpty)
            return false;

        // Inspect the complete physical light range consumed by native shading.
        // A missing or stale selected shadow is a diagnostic, not an absence.
        const EAdvancedLightRecordFlags shadowFlags = EAdvancedLightRecordFlags.Enabled | EAdvancedLightRecordFlags.CastsShadow;
        foreach (ref readonly AdvancedLightRecord light in snapshot.GlobalResources.Lights.PhysicalRecords)
            if ((light.Flags & shadowFlags) == shadowFlags &&
                ViewMatches(light.ViewMaskLo, light.ViewMaskHi, request.NativeViewIndex))
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
}
