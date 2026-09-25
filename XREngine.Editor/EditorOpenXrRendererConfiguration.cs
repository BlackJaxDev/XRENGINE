using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Editor;

/// <summary>Changes process runtime selection only while renderer devices are detached.</summary>
internal sealed class EditorOpenXrRendererConfiguration(EditorOpenXrRuntimePreparation prepared)
    : IRendererReplacementConfiguration
{
    private readonly string? _previousRuntime = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON");
    private readonly string? _previousOverride = Environment.GetEnvironmentVariable("XRE_UNIT_TEST_OPENXR_RUNTIME_JSON");
    private readonly Func<string, bool>? _previousEnsurer = RuntimeRenderingHostServices.OpenXrRuntimeServiceEnsurer;
    private readonly bool _previousRestart = RuntimeRenderingHostServices.OpenXrRecommendedDimensionsRequireServiceRestart;

    public void ApplyAfterDetach()
    {
        EnsureRuntimeCanChange();
        Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", prepared.RuntimeJsonPath);
        Environment.SetEnvironmentVariable("XRE_UNIT_TEST_OPENXR_RUNTIME_JSON", prepared.RuntimeJsonPath);
        RuntimeRenderingHostServices.OpenXrRuntimeServiceEnsurer = prepared.RuntimeServiceEnsurer;
        RuntimeRenderingHostServices.OpenXrRecommendedDimensionsRequireServiceRestart = prepared.RecommendedDimensionsRequireServiceRestart;
        OpenXRAPI.ClearVulkanRuntimeRequirementsCache();
    }

    public void RestoreBeforeRollback()
    {
        EnsureRuntimeCanChange();
        Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", _previousRuntime);
        Environment.SetEnvironmentVariable("XRE_UNIT_TEST_OPENXR_RUNTIME_JSON", _previousOverride);
        RuntimeRenderingHostServices.OpenXrRuntimeServiceEnsurer = _previousEnsurer;
        RuntimeRenderingHostServices.OpenXrRecommendedDimensionsRequireServiceRestart = _previousRestart;
        OpenXRAPI.ClearVulkanRuntimeRequirementsCache();
    }

    private static void EnsureRuntimeCanChange()
    {
        if (!OpenXRAPI.CanChangeRuntimeConfiguration)
            throw new InvalidOperationException(OpenXRAPI.RuntimeConfigurationChangeFailureReason);
    }
}
