using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.PostProcessing;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    /// <summary>Checks the canvas raster contract without changing the authored material state.</summary>
    public static string? GetWebRasterStateRejection(RenderingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.StencilTest.IsEnabled || parameters.AlphaToCoverage == ERenderParamUsage.Enabled ||
            parameters.BlendModesPerDrawBuffer is { Count: > 0 }
            ? "Stencil, multisample coverage and per-target blending are not admitted by the canvas profile."
            : null;
    }

    /// <summary>Creates a cold, detached target state; authored values win over browser defaults.</summary>
    public static PipelinePostProcessState CreateWebPostProcessAdmissionState(PipelinePostProcessState? authored)
    {
        RenderPipelinePostProcessSchemaBuilder builder = new();
        CommonPostProcessStages.AddStandardPipelineSchema(builder, defaultAutoExposure: false);
        PipelinePostProcessState state = new();
        state.BindToSchema(builder.Build());
        if (authored is not null)
            foreach ((string key, PostProcessStageState source) in authored.Stages)
                if (state.GetStage(key) is { } destination)
                    foreach ((string parameter, object? value) in source.Values)
                        destination.SetValue(parameter, value);
        return state;
    }

    /// <summary>Reports a selected feature that has no route in the shared WebGPU command chain.</summary>
    public string? GetWebPipelineFeatureRejection()
    {
        if (GlobalIlluminationMode is not (EGlobalIlluminationMode.None or EGlobalIlluminationMode.LightProbesAndIbl))
            return "The selected global illumination mode has no cooked WebGPU route.";
        if (HasFullPipelineDebugVisualization())
            return "The selected full-pipeline debug visualization has no cooked WebGPU pass.";
        return null;
    }

    /// <summary>Tests scene mesh routing; display debug callbacks have their separate admission contract.</summary>
    public static bool IsWebSceneMeshPassSupported(int pass)
        => pass is (int)EDefaultRenderPass.Background or (int)EDefaultRenderPass.OpaqueDeferred
            or (int)EDefaultRenderPass.OpaqueForward or (int)EDefaultRenderPass.MaskedForward
            or (int)EDefaultRenderPass.TransparentForward or (int)EDefaultRenderPass.PreRender
            or (int)EDefaultRenderPass.PostRender;

    /// <summary>Reads an already-bound typed state without constructing schemas or temporary settings.</summary>
    public static string? GetWebPostProcessRejection(PipelinePostProcessState? state, out string pass)
    {
        pass = CommonPostProcessStages.MotionBlurStageKey;
        if (GetSettings<MotionBlurSettings>(state) is { Enabled: true })
            return "Motion blur has no cooked WebGPU pass.";
        pass = CommonPostProcessStages.DepthOfFieldStageKey;
        if (GetSettings<DepthOfFieldSettings>(state) is { Enabled: true })
            return "Depth of field has no cooked WebGPU pass.";
        pass = CommonPostProcessStages.VolumetricFogStageKey;
        if (GetSettings<VolumetricFogSettings>(state) is { Enabled: true })
            return "Volumetric fog has no cooked WebGPU pass.";
        pass = CommonPostProcessStages.VignetteStageKey;
        if (GetSettings<VignetteSettings>(state) is { Enabled: true })
            return "Vignette has no cooked WebGPU pass.";
        pass = CommonPostProcessStages.ChromaticAberrationStageKey;
        if (GetSettings<ChromaticAberrationSettings>(state) is { Enabled: true })
            return "Chromatic aberration has no cooked WebGPU pass.";
        pass = CommonPostProcessStages.FogStageKey;
        if (GetSettings<FogSettings>(state) is { DepthFogIntensity: > 0 })
            return "Depth fog has no cooked WebGPU pass.";
        pass = CommonPostProcessStages.LensDistortionStageKey;
        if (GetSettings<LensDistortionSettings>(state) is { } lens &&
            (lens.Intensity != 0 || lens.Mode != ELensDistortionMode.None ||
                lens.ControlMode != LensDistortionSettings.LensDistortionControlMode.Artist))
            return "Lens distortion has no cooked WebGPU pass.";
        pass = CommonPostProcessStages.GpuBvhDebugStageKey;
        if (GetSettings<GpuBvhDebugSettings>(state) is { Enabled: true } or { MeshletDebugDisplayEnabled: true } or { FullOverdrawEnabled: true })
            return "The selected GPU debug visualization has no cooked WebGPU pass.";
        pass = CommonPostProcessStages.ColorGradingStageKey;
        ColorGradingSettings? color = GetSettings<ColorGradingSettings>(state);
        if (color is not null && (color.AutoExposure || color.ExposureMode != ColorGradingSettings.ExposureControlMode.Artist ||
            !float.IsFinite(color.Exposure) || !float.IsFinite(color.Gamma) || color.Exposure < 0 || color.Gamma <= 0 ||
            color.Contrast != 1 || color.Saturation != 1 || color.Brightness != 1 || color.Hue != 1 ||
            (Vector3)color.Tint != Vector3.One))
            return "Use finite manual artist exposure and positive gamma with neutral color grading.";
        pass = CommonPostProcessStages.TonemappingStageKey;
        if (GetSettings<TonemappingSettings>(state) is { Tonemapping: not ETonemappingType.Mobius })
            return "The cooked output route supports Mobius tonemapping.";
        pass = string.Empty;
        return null;
    }

    /// <summary>Checks the AO method and resolution shared by resource declaration and offline cooking.</summary>
    public static string? GetWebAmbientOcclusionRejection(AmbientOcclusionSettings? settings)
    {
        if (settings is not { Enabled: true })
            return null;
        if (AmbientOcclusionSettings.NormalizeType(settings.Type) != AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion)
            return "The selected ambient occlusion method has no cooked WebGPU effect route; select GTAO or disable AO.";
        return settings.GroundTruth.Resolution is GroundTruthAmbientOcclusionSettings.EResolution.Full
            or GroundTruthAmbientOcclusionSettings.EResolution.Half or GroundTruthAmbientOcclusionSettings.EResolution.Quarter
            ? null : "The selected GTAO resolution has no cooked WebGPU resource profile.";
    }
}
