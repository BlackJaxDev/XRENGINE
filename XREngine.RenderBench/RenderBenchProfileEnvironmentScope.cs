using XREngine.Rendering.Profiling;

namespace XREngine.RenderBench;

/// <summary>Applies explicit recipe observers before device creation and restores process overrides on teardown.</summary>
internal sealed class RenderBenchProfileEnvironmentScope : IDisposable
{
    private readonly Dictionary<string, (string? ProcessValue, bool HasRuntimeOverride, string? RuntimeValue)> _previous = new(StringComparer.Ordinal);

    public RenderBenchProfileEnvironmentScope(RenderProfileRecipe recipe)
    {
        Set(XREngineEnvironmentVariables.VulkanDiagnosticPreset, "Off");
        Set(XREngineEnvironmentVariables.VulkanDiagnosticFlags, "None");
        Set(XREngineEnvironmentVariables.VulkanGpuAssistedValidation, "0");
        Set(XREngineEnvironmentVariables.VulkanBestPracticesValidation, "0");
        Set(XREngineEnvironmentVariables.VulkanRenderDocFriendly, "0");
        Set(XREngineEnvironmentVariables.VulkanCrashBreadcrumbs, "0");
        Set(XREngineEnvironmentVariables.VulkanRecordingDiag, "0");
        Set(XREngineEnvironmentVariables.VulkanRecordingProfileDetail, "0");
        Set(XREngineEnvironmentVariables.GpuTimestampDense, "0");
        Set(XREngineEnvironmentVariables.VulkanRenderBenchGpuCalibration, recipe.GpuProfiling.CalibratedTimestamps ? "1" : "0");
        Set(XREngineEnvironmentVariables.VulkanRenderBenchPerformanceQuery, recipe.HardwareCounterPolicy != RenderProfileHardwareCounterPolicy.Disabled ||
            recipe.Instrumentation.HasFlag(RenderProfileInstrumentation.HardwareCounters) ? "1" : "0");
        Set(XREngineEnvironmentVariables.VulkanSynchronizationValidation, recipe.EnableSynchronizationValidation ? "1" : "0");
        Set(XREngineEnvironmentVariables.VulkanValidation, recipe.EnableValidation ? "1" : "0");
        Set(XREngineEnvironmentVariables.VulkanCommandBufferLabels, recipe.LabelPolicy == RenderProfileLabelPolicy.Disabled ? "0" : "1");
        if (recipe.Fixture.Equals("production-default-static", StringComparison.OrdinalIgnoreCase))
        {
            Set(XREngineEnvironmentVariables.P3Logging, "0");
            Set(XREngineEnvironmentVariables.BucketLoopDryRun, "0");
            Set(XREngineEnvironmentVariables.SkipCommandSwapIfClean, "0");
            Set(XREngineEnvironmentVariables.BucketLoopSkipEmpty, "0");
            Set(XREngineEnvironmentVariables.ForceSingleBucket, "0");
            Set(XREngineEnvironmentVariables.MdicGlFinish, "0");
            Set(XREngineEnvironmentVariables.EngineAssetsPath, Path.Combine(Environment.CurrentDirectory, "Build", "CommonAssets"));
            Set(XREngineEnvironmentVariables.ForceMeshSubmissionStrategy, "GpuIndirectZeroReadback");
            Set(XREngineEnvironmentVariables.GpuDrivenValidationCapacityMultiplier, "1");
            Set(XREngineEnvironmentVariables.GpuDrivenValidationCapacityFloor, "0");
            Set(XREngineEnvironmentVariables.OcclusionCullingMode, "Disabled");
            Set(XREngineEnvironmentVariables.ZeroReadbackMaterialDrawPath, "BindlessMaterialTable");
        }
    }

    private void Set(string name, string value)
    {
        bool hasRuntimeOverride = XREnvironment.TryGetRuntimeOverride(name, out string? runtimeValue);
        _previous.Add(name, (Environment.GetEnvironmentVariable(name), hasRuntimeOverride, runtimeValue));
        XREnvironment.SetRuntimeOverride(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, previous) in _previous)
        {
            if (previous.HasRuntimeOverride)
                XREnvironment.SetRuntimeOverride(name, previous.RuntimeValue);
            else
                XREnvironment.ClearRuntimeOverride(name);
            Environment.SetEnvironmentVariable(name, previous.ProcessValue);
        }
        XREnvironment.RefreshFromProcess();
        _previous.Clear();
    }
}
