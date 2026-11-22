using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Editor;

/// <summary>Changes process runtime selection only while renderer devices are detached.</summary>
internal sealed class EditorOpenXrRendererConfiguration(EditorOpenXrRuntimePreparation prepared)
    : IRendererReplacementConfiguration
{
    private readonly string? _previousRuntime = XREnvironment.GetValue(XREngineEnvironmentVariables.XrRuntimeJson);
    private readonly string? _previousOverride = XREnvironment.GetValue(XREngineEnvironmentVariables.UnitTestOpenXrRuntimeJson);
    private readonly Func<string, bool>? _previousEnsurer = RuntimeRenderingHostServices.OpenXrRuntimeServiceEnsurer;
    private readonly bool _previousRestart = RuntimeRenderingHostServices.OpenXrRecommendedDimensionsRequireServiceRestart;

    public void ApplyAfterDetach()
    {
        EnsureRuntimeCanChange();
        XREnvironment.SetProcessValue(XREngineEnvironmentVariables.XrRuntimeJson, prepared.RuntimeJsonPath);
        XREnvironment.SetProcessValue(XREngineEnvironmentVariables.UnitTestOpenXrRuntimeJson, prepared.RuntimeJsonPath);
        RuntimeRenderingHostServices.OpenXrRuntimeServiceEnsurer = prepared.RuntimeServiceEnsurer;
        RuntimeRenderingHostServices.OpenXrRecommendedDimensionsRequireServiceRestart = prepared.RecommendedDimensionsRequireServiceRestart;
        OpenXrRuntimeSettings.ClearVulkanRuntimeRequirementsCache();
    }

    public void RestoreBeforeRollback()
    {
        EnsureRuntimeCanChange();
        XREnvironment.SetProcessValue(XREngineEnvironmentVariables.XrRuntimeJson, _previousRuntime);
        XREnvironment.SetProcessValue(XREngineEnvironmentVariables.UnitTestOpenXrRuntimeJson, _previousOverride);
        RuntimeRenderingHostServices.OpenXrRuntimeServiceEnsurer = _previousEnsurer;
        RuntimeRenderingHostServices.OpenXrRecommendedDimensionsRequireServiceRestart = _previousRestart;
        OpenXrRuntimeSettings.ClearVulkanRuntimeRequirementsCache();
    }

    private static void EnsureRuntimeCanChange()
    {
        if (!OpenXrRuntimeSettings.CanChangeRuntimeConfiguration)
            throw new InvalidOperationException(OpenXrRuntimeSettings.RuntimeConfigurationChangeFailureReason);
    }
}
