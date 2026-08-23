using System.Reflection;

namespace XREngine;

/// <summary>
/// Complete editor catalog of environment variables declared by
/// <see cref="XREngineEnvironmentVariables"/>.
/// </summary>
public static class XREngineEnvironmentVariableCatalog
{
    private static readonly HashSet<string> SensitiveFields =
    [
        nameof(XREngineEnvironmentVariables.OpenAiApiKey),
        nameof(XREngineEnvironmentVariables.AnthropicApiKey),
        nameof(XREngineEnvironmentVariables.GeminiApiKey),
        nameof(XREngineEnvironmentVariables.GitHubToken),
        nameof(XREngineEnvironmentVariables.SessionToken),
        nameof(XREngineEnvironmentVariables.RealtimeJoinPayload),
    ];

    private static readonly HashSet<string> TextOrEnumFields =
    [
        nameof(XREngineEnvironmentVariables.FirstChanceExceptions),
        nameof(XREngineEnvironmentVariables.ForceMeshSubmissionStrategy),
        nameof(XREngineEnvironmentVariables.ForceCpuIndirectBuild),
        nameof(XREngineEnvironmentVariables.ForceGpuBvhCulling),
        nameof(XREngineEnvironmentVariables.ForceGpuBvhRebuildEveryFrame),
        nameof(XREngineEnvironmentVariables.ModelRenderDiagFilter),
        nameof(XREngineEnvironmentVariables.ModelDrawDiagFilter),
        nameof(XREngineEnvironmentVariables.OutputSourceFbo),
        nameof(XREngineEnvironmentVariables.RenderWorkerQos),
        nameof(XREngineEnvironmentVariables.VulkanDiagnosticFlags),
        nameof(XREngineEnvironmentVariables.VulkanLoaderLayersDisable),
        nameof(XREngineEnvironmentVariables.VulkanExternalValidationAllowlist),
        nameof(XREngineEnvironmentVariables.WindowTitle),
    ];

    private static readonly HashSet<string> KnownAutomaticFeatureFields =
    [
        nameof(XREngineEnvironmentVariables.AdvancedRenderPipelineMode),
        nameof(XREngineEnvironmentVariables.DirectStorageEnabled),
        nameof(XREngineEnvironmentVariables.EnableVulkanUpscaleBridge),
        nameof(XREngineEnvironmentVariables.ShaderSourceOptimizer),
        nameof(XREngineEnvironmentVariables.TextureStreamingCacheWarmupEnabled),
        nameof(XREngineEnvironmentVariables.VkEnableAutoUniformRewrite),
        nameof(XREngineEnvironmentVariables.VulkanAsyncTextureUpload),
        nameof(XREngineEnvironmentVariables.VulkanCommandChainStabilityGuard),
        nameof(XREngineEnvironmentVariables.VulkanCommandChains),
        nameof(XREngineEnvironmentVariables.VulkanDynamicUniformBuffer),
        nameof(XREngineEnvironmentVariables.VulkanPrimaryCommandBufferReuse),
        nameof(XREngineEnvironmentVariables.VulkanTextureUploadPrepWorker),
        nameof(XREngineEnvironmentVariables.VulkanTextureUploadTransferQueue),
    ];

    private static readonly Dictionary<string, RuntimeEnvironmentVariableDescriptor> ByName;

    static XREngineEnvironmentVariableCatalog()
    {
        RuntimeEnvironmentVariableDescriptor[] descriptors = typeof(XREngineEnvironmentVariables)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(CreateDescriptor)
            .OrderBy(static descriptor => descriptor.Category)
            .ThenBy(static descriptor => descriptor.Name, StringComparer.Ordinal)
            .ToArray();

        All = descriptors;
        ByName = descriptors.ToDictionary(static descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<RuntimeEnvironmentVariableDescriptor> All { get; }

    public static RuntimeEnvironmentVariableDescriptor? Find(string name)
        => ByName.TryGetValue(name, out RuntimeEnvironmentVariableDescriptor? descriptor)
            ? descriptor
            : null;

    private static RuntimeEnvironmentVariableDescriptor CreateDescriptor(FieldInfo field)
    {
        string fieldName = field.Name;
        string name = (string)field.GetRawConstantValue()!;
        RuntimeEnvironmentCategory category = ResolveCategory(fieldName, name);
        bool diagnostic = IsDiagnosticOrValidation(fieldName, category);
        bool downgrade = IsDowngradeOverride(fieldName);
        RuntimeEnvironmentValueKind valueKind = ResolveValueKind(fieldName, name);
        RuntimeEnvironmentApplyMode applyMode = ResolveApplyMode(fieldName, category, diagnostic);
        string defaultBehavior = ResolveDefaultBehavior(fieldName, diagnostic, downgrade);
        return new(
            fieldName,
            name,
            category,
            valueKind,
            applyMode,
            diagnostic,
            downgrade,
            defaultBehavior);
    }

    private static RuntimeEnvironmentCategory ResolveCategory(string fieldName, string name)
    {
        if (fieldName is nameof(XREngineEnvironmentVariables.Path) or
            nameof(XREngineEnvironmentVariables.ContinuousIntegration) ||
            fieldName.StartsWith("Dotnet", StringComparison.Ordinal) ||
            fieldName.StartsWith("MsBuild", StringComparison.Ordinal))
        {
            return RuntimeEnvironmentCategory.System;
        }

        if (fieldName.Contains("UnitTest", StringComparison.Ordinal) ||
            fieldName is nameof(XREngineEnvironmentVariables.HeadlessTest) or
                nameof(XREngineEnvironmentVariables.HideTestWindows) or
                nameof(XREngineEnvironmentVariables.ShowTestWindows) or
                nameof(XREngineEnvironmentVariables.ShowGlTest) or
                nameof(XREngineEnvironmentVariables.ShowTestNoBlock) or
                nameof(XREngineEnvironmentVariables.ShowTestBlock) or
                nameof(XREngineEnvironmentVariables.ShowWindowDurationMs) or
                nameof(XREngineEnvironmentVariables.ShowTestWindowMs) or
                nameof(XREngineEnvironmentVariables.ShowGlTestMs) or
                nameof(XREngineEnvironmentVariables.DisableImGuiFileDialogs) or
                nameof(XREngineEnvironmentVariables.PhysicsDebugPreset))
        {
            return RuntimeEnvironmentCategory.UnitTesting;
        }

        if (fieldName.Contains("OpenXr", StringComparison.Ordinal) ||
            fieldName.StartsWith("Xr", StringComparison.Ordinal) ||
            fieldName.StartsWith("Monado", StringComparison.Ordinal))
        {
            return RuntimeEnvironmentCategory.OpenXR;
        }

        if (fieldName.StartsWith("Vulkan", StringComparison.Ordinal) ||
            fieldName.StartsWith("Vk", StringComparison.Ordinal) ||
            name.StartsWith("VK_", StringComparison.Ordinal))
        {
            return RuntimeEnvironmentCategory.Vulkan;
        }

        if (fieldName.Contains("OpenGl", StringComparison.Ordinal) ||
            fieldName.StartsWith("Gl", StringComparison.Ordinal) ||
            fieldName.Contains("Shader", StringComparison.Ordinal) ||
            fieldName.Contains("ProgramBinary", StringComparison.Ordinal) ||
            fieldName.Contains("SharedContext", StringComparison.Ordinal))
        {
            return RuntimeEnvironmentCategory.OpenGL;
        }

        if (fieldName.Contains("Profile", StringComparison.Ordinal) ||
            fieldName.Contains("Profiler", StringComparison.Ordinal) ||
            fieldName.Contains("Benchmark", StringComparison.Ordinal) ||
            fieldName is nameof(XREngineEnvironmentVariables.GpuTimestampDense))
        {
            return RuntimeEnvironmentCategory.Profiling;
        }

        if (fieldName.Contains("Udp", StringComparison.Ordinal) ||
            fieldName.Contains("Session", StringComparison.Ordinal) ||
            fieldName.Contains("WorldId", StringComparison.Ordinal) ||
            fieldName.Contains("WorldRevision", StringComparison.Ordinal) ||
            fieldName.Contains("WorldContent", StringComparison.Ordinal) ||
            fieldName.Contains("WorldAsset", StringComparison.Ordinal) ||
            fieldName.Contains("WorldRequired", StringComparison.Ordinal) ||
            fieldName.Contains("RealtimeJoin", StringComparison.Ordinal) ||
            fieldName.Contains("Pose", StringComparison.Ordinal) ||
            fieldName is nameof(XREngineEnvironmentVariables.NetMode))
        {
            return RuntimeEnvironmentCategory.Networking;
        }

        if (fieldName.Contains("AssetsPath", StringComparison.Ordinal) ||
            fieldName.Contains("CachePath", StringComparison.Ordinal) ||
            fieldName.Contains("MetadataPath", StringComparison.Ordinal) ||
            fieldName.Contains("StreamUrl", StringComparison.Ordinal) ||
            fieldName.Contains("Fbx", StringComparison.Ordinal) ||
            fieldName.Contains("Coacd", StringComparison.Ordinal) ||
            fieldName.Contains("DirectStorage", StringComparison.Ordinal) ||
            fieldName.Contains("TextureCache", StringComparison.Ordinal))
        {
            return RuntimeEnvironmentCategory.Assets;
        }

        if (IsDiagnosticOrValidation(fieldName, RuntimeEnvironmentCategory.Other))
            return RuntimeEnvironmentCategory.Diagnostics;

        if (fieldName is nameof(XREngineEnvironmentVariables.WorldMode) or
            nameof(XREngineEnvironmentVariables.WindowTitle) or
            nameof(XREngineEnvironmentVariables.WindowPumpHost) or
            nameof(XREngineEnvironmentVariables.VrClientGameName))
        {
            return RuntimeEnvironmentCategory.Startup;
        }

        if (fieldName.Contains("AgentValidation", StringComparison.Ordinal) ||
            fieldName.Contains("Smoke", StringComparison.Ordinal) ||
            fieldName.Contains("CookCommon", StringComparison.Ordinal))
        {
            return RuntimeEnvironmentCategory.Tooling;
        }

        return RuntimeEnvironmentCategory.Rendering;
    }

    private static RuntimeEnvironmentValueKind ResolveValueKind(string fieldName, string name)
    {
        if (SensitiveFields.Contains(fieldName))
            return RuntimeEnvironmentValueKind.Secret;

        if (fieldName is nameof(XREngineEnvironmentVariables.Path) ||
            fieldName.EndsWith("Path", StringComparison.Ordinal) ||
            fieldName.EndsWith("Root", StringComparison.Ordinal) ||
            fieldName.EndsWith("Directory", StringComparison.Ordinal) ||
            name.EndsWith("_DIR", StringComparison.Ordinal))
        {
            return RuntimeEnvironmentValueKind.Path;
        }

        if (TextOrEnumFields.Contains(fieldName))
            return fieldName.Contains("Mode", StringComparison.Ordinal) ||
                   fieldName.Contains("Strategy", StringComparison.Ordinal) ||
                   fieldName.Contains("Policy", StringComparison.Ordinal) ||
                   fieldName.Contains("Backend", StringComparison.Ordinal) ||
                   fieldName.Contains("Preset", StringComparison.Ordinal) ||
                   fieldName.EndsWith("Qos", StringComparison.Ordinal)
                ? RuntimeEnvironmentValueKind.Enum
                : RuntimeEnvironmentValueKind.Text;

        if (fieldName.Contains("Mode", StringComparison.Ordinal) ||
            fieldName.Contains("Strategy", StringComparison.Ordinal) ||
            fieldName.Contains("Policy", StringComparison.Ordinal) ||
            fieldName.Contains("Backend", StringComparison.Ordinal) ||
            fieldName.Contains("Preset", StringComparison.Ordinal) ||
            fieldName.EndsWith("Kind", StringComparison.Ordinal) ||
            fieldName.EndsWith("Role", StringComparison.Ordinal) ||
            fieldName.EndsWith("Api", StringComparison.Ordinal))
        {
            return RuntimeEnvironmentValueKind.Enum;
        }

        if (fieldName.EndsWith("Ms", StringComparison.Ordinal) ||
            fieldName.EndsWith("Port", StringComparison.Ordinal) ||
            fieldName.EndsWith("Frames", StringComparison.Ordinal) ||
            fieldName.EndsWith("Width", StringComparison.Ordinal) ||
            fieldName.EndsWith("Height", StringComparison.Ordinal) ||
            fieldName.EndsWith("Bytes", StringComparison.Ordinal) ||
            fieldName.EndsWith("Cap", StringComparison.Ordinal) ||
            fieldName.EndsWith("Limit", StringComparison.Ordinal) ||
            fieldName.EndsWith("Threads", StringComparison.Ordinal) ||
            fieldName.EndsWith("Workers", StringComparison.Ordinal) ||
            fieldName.EndsWith("Count", StringComparison.Ordinal))
        {
            return RuntimeEnvironmentValueKind.Integer;
        }

        if (fieldName.Contains("Scale", StringComparison.Ordinal) ||
            fieldName.Contains("Seconds", StringComparison.Ordinal) ||
            fieldName.Contains("RefreshHz", StringComparison.Ordinal) ||
            fieldName.Contains("Fps", StringComparison.Ordinal) ||
            fieldName.Contains("Multiplier", StringComparison.Ordinal))
        {
            return RuntimeEnvironmentValueKind.Number;
        }

        if (LooksBoolean(fieldName))
            return RuntimeEnvironmentValueKind.Boolean;

        return RuntimeEnvironmentValueKind.Text;
    }

    private static bool LooksBoolean(string fieldName)
        => fieldName.StartsWith("Enable", StringComparison.Ordinal) ||
           fieldName.StartsWith("Disable", StringComparison.Ordinal) ||
           fieldName.StartsWith("Allow", StringComparison.Ordinal) ||
           fieldName.StartsWith("Hide", StringComparison.Ordinal) ||
           fieldName.StartsWith("Show", StringComparison.Ordinal) ||
           fieldName.StartsWith("Skip", StringComparison.Ordinal) ||
           fieldName.StartsWith("Bypass", StringComparison.Ordinal) ||
           fieldName.StartsWith("Force", StringComparison.Ordinal) ||
           fieldName.Contains("Validation", StringComparison.Ordinal) ||
           fieldName.Contains("Diagnostics", StringComparison.Ordinal) ||
           fieldName.Contains("Trace", StringComparison.Ordinal) ||
           fieldName.Contains("Logging", StringComparison.Ordinal) ||
           fieldName.Contains("Debug", StringComparison.Ordinal) ||
           fieldName.Contains("Capture", StringComparison.Ordinal) ||
           fieldName.EndsWith("Enabled", StringComparison.Ordinal) ||
           fieldName.EndsWith("Reuse", StringComparison.Ordinal) ||
           fieldName.EndsWith("Worker", StringComparison.Ordinal) ||
           fieldName.EndsWith("Polling", StringComparison.Ordinal);

    private static bool IsDiagnosticOrValidation(
        string fieldName,
        RuntimeEnvironmentCategory category)
        => category == RuntimeEnvironmentCategory.Profiling ||
           fieldName.Contains("Diag", StringComparison.OrdinalIgnoreCase) ||
           fieldName.Contains("Debug", StringComparison.OrdinalIgnoreCase) ||
           fieldName.Contains("Trace", StringComparison.OrdinalIgnoreCase) ||
           fieldName.Contains("Validation", StringComparison.OrdinalIgnoreCase) ||
           fieldName.Contains("Validate", StringComparison.OrdinalIgnoreCase) ||
           fieldName.Contains("Audit", StringComparison.OrdinalIgnoreCase) ||
           fieldName.Contains("Breadcrumb", StringComparison.OrdinalIgnoreCase) ||
           fieldName.Contains("Capture", StringComparison.OrdinalIgnoreCase) ||
           fieldName.Contains("Dump", StringComparison.OrdinalIgnoreCase) ||
           fieldName.Contains("Smoke", StringComparison.OrdinalIgnoreCase) ||
           fieldName.Contains("Benchmark", StringComparison.OrdinalIgnoreCase) ||
           fieldName is nameof(XREngineEnvironmentVariables.FirstChanceExceptions) or
               nameof(XREngineEnvironmentVariables.P3Logging) or
               nameof(XREngineEnvironmentVariables.BucketLoopDryRun);

    private static bool IsDowngradeOverride(string fieldName)
        => fieldName.StartsWith("Disable", StringComparison.Ordinal) ||
           fieldName.StartsWith("Skip", StringComparison.Ordinal) ||
           fieldName.StartsWith("Bypass", StringComparison.Ordinal) ||
           fieldName is nameof(XREngineEnvironmentVariables.ForceCpuIndirectBuild) or
               nameof(XREngineEnvironmentVariables.VulkanAllowCpuMeshSafetyNet) or
               nameof(XREngineEnvironmentVariables.VulkanImportedTexturePreviewFreeze) or
               nameof(XREngineEnvironmentVariables.OpenXrVulkanMirrorFbo) or
               nameof(XREngineEnvironmentVariables.OpenXrVulkanSerialEyeSubmit);

    private static RuntimeEnvironmentApplyMode ResolveApplyMode(
        string fieldName,
        RuntimeEnvironmentCategory category,
        bool diagnostic)
    {
        if (category == RuntimeEnvironmentCategory.System ||
            category == RuntimeEnvironmentCategory.Startup ||
            fieldName.Contains("EditorSession", StringComparison.Ordinal) ||
            fieldName.Contains("WorldMode", StringComparison.Ordinal) ||
            fieldName.Contains("WorldSettingsPath", StringComparison.Ordinal) ||
            fieldName.Contains("JobWorker", StringComparison.Ordinal) ||
            fieldName.Contains("WorkerThread", StringComparison.Ordinal) ||
            fieldName is nameof(XREngineEnvironmentVariables.ReservedForegroundThreads) or
                nameof(XREngineEnvironmentVariables.AllowCpuOversubscription) or
                nameof(XREngineEnvironmentVariables.RenderWorkerQos) ||
            fieldName is nameof(XREngineEnvironmentVariables.VulkanCommandChainWorkerCount) ||
            fieldName.Contains("GcLatency", StringComparison.Ordinal) ||
            fieldName.Contains("MemoryProfile", StringComparison.Ordinal))
        {
            return RuntimeEnvironmentApplyMode.ProcessRestart;
        }

        if (category == RuntimeEnvironmentCategory.OpenXR)
            return RuntimeEnvironmentApplyMode.OpenXrSessionRestart;

        if (category == RuntimeEnvironmentCategory.Vulkan &&
            (fieldName.Contains("Diagnostic", StringComparison.Ordinal) ||
             fieldName.Contains("Validation", StringComparison.Ordinal) ||
             fieldName.Contains("Capability", StringComparison.Ordinal) ||
             fieldName.Contains("DescriptorBackend", StringComparison.Ordinal) ||
             fieldName.Contains("ProgramBindingBackend", StringComparison.Ordinal) ||
             fieldName.Contains("RayTracingBackend", StringComparison.Ordinal) ||
             fieldName.Contains("FoveationBackend", StringComparison.Ordinal) ||
             fieldName.Contains("ObsHook", StringComparison.Ordinal)))
        {
            return RuntimeEnvironmentApplyMode.RendererRestart;
        }

        if (fieldName is nameof(XREngineEnvironmentVariables.GlDebug) or
            nameof(XREngineEnvironmentVariables.DisableOpenGlCompileLinkWorkerPool) or
            nameof(XREngineEnvironmentVariables.DisableSharedContextLinkQueue))
        {
            return RuntimeEnvironmentApplyMode.RendererRestart;
        }

        return diagnostic
            ? RuntimeEnvironmentApplyMode.Immediate
            : RuntimeEnvironmentApplyMode.NextOperation;
    }

    private static string ResolveDefaultBehavior(
        string fieldName,
        bool diagnostic,
        bool downgrade)
    {
        if (diagnostic)
            return "Unset/off; diagnostics and validation are opt-in.";
        if (downgrade)
            return "Unset/off; the normal best-available path remains enabled.";
        if (KnownAutomaticFeatureFields.Contains(fieldName))
            return "Unset uses the best available implementation and downgrades only when unavailable.";
        return "Unset uses the engine or subsystem default.";
    }
}
