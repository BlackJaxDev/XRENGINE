using System.Text;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanDiagnosticOptions
{
    public EVulkanDiagnosticPreset Preset { get; init; }
    public EVulkanDiagnosticFlags Flags { get; init; }
    public string SourceSummary { get; init; }
    public string OverheadWarnings { get; init; }
    public int DeviceFaultAddressRecordCap { get; init; }
    public int DeviceFaultVendorRecordCap { get; init; }
    public int DeviceFaultReportCap { get; init; }
    public int DeviceFaultVendorBinaryByteCap { get; init; }

    public bool EnableValidationLayers =>
        Flags.HasFlag(EVulkanDiagnosticFlags.StandardValidation) ||
        Flags.HasFlag(EVulkanDiagnosticFlags.SynchronizationValidation) ||
        Flags.HasFlag(EVulkanDiagnosticFlags.GpuAssistedValidation) ||
        Flags.HasFlag(EVulkanDiagnosticFlags.BestPractices);

    public bool EnableSynchronizationValidation => Flags.HasFlag(EVulkanDiagnosticFlags.SynchronizationValidation);
    public bool EnableGpuAssistedValidation => Flags.HasFlag(EVulkanDiagnosticFlags.GpuAssistedValidation);
    public bool EnableBestPractices => Flags.HasFlag(EVulkanDiagnosticFlags.BestPractices);
    public bool EnableDebugUtils => EnableValidationLayers || Flags.HasFlag(EVulkanDiagnosticFlags.DebugUtils);
    public bool EnableCommandBufferLabels => Flags.HasFlag(EVulkanDiagnosticFlags.CommandBufferLabels);
    public bool EnableCrashBreadcrumbs => Flags.HasFlag(EVulkanDiagnosticFlags.CrashBreadcrumbs);
    public bool RequestDeviceFault => Flags.HasFlag(EVulkanDiagnosticFlags.DeviceFault);
    public bool RequestDeviceFaultDeviceLostOnMasked => Flags.HasFlag(EVulkanDiagnosticFlags.DeviceFaultDeviceLostOnMasked);
    public bool RequestDeviceAddressBindingReport => Flags.HasFlag(EVulkanDiagnosticFlags.DeviceAddressBindingReport);
    public bool RequestNvDiagnosticCheckpoints => Flags.HasFlag(EVulkanDiagnosticFlags.NvDiagnosticCheckpoints);
    public bool RequestNvDiagnosticsConfig => Flags.HasFlag(EVulkanDiagnosticFlags.NvDiagnosticsConfig);
    public bool RenderDocFriendly => Flags.HasFlag(EVulkanDiagnosticFlags.RenderDocFriendly);
    public bool HasValidationFeatures =>
        EnableSynchronizationValidation ||
        EnableGpuAssistedValidation ||
        EnableBestPractices;

    public static VulkanDiagnosticOptions Resolve()
    {
        EVulkanDiagnosticPreset preset = RuntimeEngine.EffectiveSettings.VulkanDiagnosticPreset;
        EVulkanDiagnosticFlags flags = FlagsForPreset(preset) | RuntimeEngine.EffectiveSettings.VulkanDiagnosticFlags;

        StringBuilder sources = new();
        sources.Append("settings preset=").Append(preset).Append(" flags=").Append(RuntimeEngine.EffectiveSettings.VulkanDiagnosticFlags);

        ApplyPresetEnvOverride(ref preset, ref flags, sources);
        ApplyFlagsEnvOverride(ref flags, sources);

        ApplyBooleanEnvOverride(
            global::XREngine.XREngineEnvironmentVariables.VulkanValidation,
            EVulkanDiagnosticFlags.StandardValidation | EVulkanDiagnosticFlags.DebugUtils,
            ref flags,
            sources);
        ApplyBooleanEnvOverride(
            global::XREngine.XREngineEnvironmentVariables.VulkanSynchronizationValidation,
            EVulkanDiagnosticFlags.StandardValidation | EVulkanDiagnosticFlags.SynchronizationValidation | EVulkanDiagnosticFlags.DebugUtils,
            ref flags,
            sources);
        ApplyBooleanEnvOverride(
            global::XREngine.XREngineEnvironmentVariables.VulkanGpuAssistedValidation,
            EVulkanDiagnosticFlags.StandardValidation | EVulkanDiagnosticFlags.GpuAssistedValidation | EVulkanDiagnosticFlags.DebugUtils,
            ref flags,
            sources);
        ApplyBooleanEnvOverride(
            global::XREngine.XREngineEnvironmentVariables.VulkanBestPracticesValidation,
            EVulkanDiagnosticFlags.StandardValidation | EVulkanDiagnosticFlags.BestPractices | EVulkanDiagnosticFlags.DebugUtils,
            ref flags,
            sources);
        ApplyBooleanEnvOverride(
            global::XREngine.XREngineEnvironmentVariables.VulkanCommandBufferLabels,
            EVulkanDiagnosticFlags.DebugUtils | EVulkanDiagnosticFlags.CommandBufferLabels,
            ref flags,
            sources);
        ApplyBooleanEnvOverride(
            global::XREngine.XREngineEnvironmentVariables.VulkanCrashBreadcrumbs,
            EVulkanDiagnosticFlags.CrashBreadcrumbs,
            ref flags,
            sources);
        ApplyBooleanEnvOverride(
            global::XREngine.XREngineEnvironmentVariables.VulkanDeviceFault,
            EVulkanDiagnosticFlags.DeviceFault,
            ref flags,
            sources);
        ApplyBooleanEnvOverride(
            global::XREngine.XREngineEnvironmentVariables.VulkanDeviceAddressBindingReport,
            EVulkanDiagnosticFlags.DeviceAddressBindingReport,
            ref flags,
            sources);
        ApplyBooleanEnvOverride(
            global::XREngine.XREngineEnvironmentVariables.VulkanNvDiagnosticCheckpoints,
            EVulkanDiagnosticFlags.NvDiagnosticCheckpoints,
            ref flags,
            sources);
        ApplyBooleanEnvOverride(
            global::XREngine.XREngineEnvironmentVariables.VulkanNvDiagnosticsConfig,
            EVulkanDiagnosticFlags.NvDiagnosticsConfig,
            ref flags,
            sources);
        ApplyBooleanEnvOverride(
            global::XREngine.XREngineEnvironmentVariables.VulkanRenderDocFriendly,
            EVulkanDiagnosticFlags.RenderDocFriendly | EVulkanDiagnosticFlags.DebugUtils | EVulkanDiagnosticFlags.CommandBufferLabels,
            ref flags,
            sources);

        if (flags.HasFlag(EVulkanDiagnosticFlags.SynchronizationValidation) ||
            flags.HasFlag(EVulkanDiagnosticFlags.GpuAssistedValidation) ||
            flags.HasFlag(EVulkanDiagnosticFlags.BestPractices))
        {
            flags |= EVulkanDiagnosticFlags.StandardValidation | EVulkanDiagnosticFlags.DebugUtils;
        }

        if (flags.HasFlag(EVulkanDiagnosticFlags.CommandBufferLabels))
            flags |= EVulkanDiagnosticFlags.DebugUtils;

        int addressRecordCap = ResolvePositiveIntEnv(
            global::XREngine.XREngineEnvironmentVariables.VulkanDeviceFaultAddressRecordCap,
            defaultValue: 256,
            maximumValue: 4096,
            sources);
        int vendorRecordCap = ResolvePositiveIntEnv(
            global::XREngine.XREngineEnvironmentVariables.VulkanDeviceFaultVendorRecordCap,
            defaultValue: 256,
            maximumValue: 4096,
            sources);
        int reportCap = ResolvePositiveIntEnv(
            global::XREngine.XREngineEnvironmentVariables.VulkanDeviceFaultReportCap,
            defaultValue: 64,
            maximumValue: 1024,
            sources);
        int vendorBinaryByteCap = ResolvePositiveIntEnv(
            global::XREngine.XREngineEnvironmentVariables.VulkanDeviceFaultVendorBinaryByteCap,
            defaultValue: 64 * 1024 * 1024,
            maximumValue: 1024 * 1024 * 1024,
            sources);

        return new()
        {
            Preset = preset,
            Flags = flags,
            SourceSummary = sources.ToString(),
            OverheadWarnings = BuildOverheadWarnings(flags),
            DeviceFaultAddressRecordCap = addressRecordCap,
            DeviceFaultVendorRecordCap = vendorRecordCap,
            DeviceFaultReportCap = reportCap,
            DeviceFaultVendorBinaryByteCap = vendorBinaryByteCap,
        };
    }

    private static int ResolvePositiveIntEnv(
        string variableName,
        int defaultValue,
        int maximumValue,
        StringBuilder sources)
    {
        string? raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (!int.TryParse(raw.Trim(), out int value) || value <= 0)
        {
            sources.Append("; ").Append(variableName).Append("=<invalid:").Append(raw.Trim()).Append('>');
            return defaultValue;
        }

        int capped = NormalizePositiveCap(value, defaultValue, maximumValue);
        sources.Append("; ").Append(variableName).Append('=').Append(capped);
        return capped;
    }

    internal static int NormalizePositiveCap(int value, int defaultValue, int maximumValue)
        => value <= 0 ? defaultValue : Math.Min(value, maximumValue);

    private static EVulkanDiagnosticFlags FlagsForPreset(EVulkanDiagnosticPreset preset)
        => preset switch
        {
            EVulkanDiagnosticPreset.StandardValidation =>
                EVulkanDiagnosticFlags.StandardValidation |
                EVulkanDiagnosticFlags.DebugUtils,
            EVulkanDiagnosticPreset.SyncValidation =>
                EVulkanDiagnosticFlags.StandardValidation |
                EVulkanDiagnosticFlags.SynchronizationValidation |
                EVulkanDiagnosticFlags.DebugUtils |
                EVulkanDiagnosticFlags.CrashBreadcrumbs,
            EVulkanDiagnosticPreset.GpuAssisted =>
                EVulkanDiagnosticFlags.StandardValidation |
                EVulkanDiagnosticFlags.GpuAssistedValidation |
                EVulkanDiagnosticFlags.DebugUtils |
                EVulkanDiagnosticFlags.CrashBreadcrumbs,
            EVulkanDiagnosticPreset.BestPractices =>
                EVulkanDiagnosticFlags.StandardValidation |
                EVulkanDiagnosticFlags.BestPractices |
                EVulkanDiagnosticFlags.DebugUtils,
            EVulkanDiagnosticPreset.CrashDiagnostics =>
                EVulkanDiagnosticFlags.DebugUtils |
                EVulkanDiagnosticFlags.CrashBreadcrumbs |
                EVulkanDiagnosticFlags.DeviceFault |
                EVulkanDiagnosticFlags.DeviceAddressBindingReport |
                EVulkanDiagnosticFlags.NvDiagnosticCheckpoints |
                EVulkanDiagnosticFlags.NvDiagnosticsConfig,
            EVulkanDiagnosticPreset.RenderDocFriendly =>
                EVulkanDiagnosticFlags.DebugUtils |
                EVulkanDiagnosticFlags.CommandBufferLabels |
                EVulkanDiagnosticFlags.CrashBreadcrumbs |
                EVulkanDiagnosticFlags.RenderDocFriendly,
            _ => EVulkanDiagnosticFlags.None,
        };

    private static void ApplyPresetEnvOverride(
        ref EVulkanDiagnosticPreset preset,
        ref EVulkanDiagnosticFlags flags,
        StringBuilder sources)
    {
        string? raw = Environment.GetEnvironmentVariable(global::XREngine.XREngineEnvironmentVariables.VulkanDiagnosticPreset);
        if (string.IsNullOrWhiteSpace(raw))
            return;

        if (!TryParsePreset(raw, out EVulkanDiagnosticPreset parsed))
        {
            sources.Append("; ")
                .Append(global::XREngine.XREngineEnvironmentVariables.VulkanDiagnosticPreset)
                .Append("=<invalid:")
                .Append(raw.Trim())
                .Append('>');
            return;
        }

        preset = parsed;
        flags = FlagsForPreset(parsed);
        sources.Append("; ")
            .Append(global::XREngine.XREngineEnvironmentVariables.VulkanDiagnosticPreset)
            .Append('=')
            .Append(parsed);
    }

    private static void ApplyFlagsEnvOverride(ref EVulkanDiagnosticFlags flags, StringBuilder sources)
    {
        string? raw = Environment.GetEnvironmentVariable(global::XREngine.XREngineEnvironmentVariables.VulkanDiagnosticFlags);
        if (string.IsNullOrWhiteSpace(raw))
            return;

        string[] tokens = raw.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            bool remove = token.Length > 1 && token[0] == '-';
            if (remove || token.Length > 1 && token[0] == '+')
                token = token[1..];

            if (IsFalseToken(token) || token.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                flags = EVulkanDiagnosticFlags.None;
                continue;
            }

            if (!TryParseFlag(token, out EVulkanDiagnosticFlags parsed))
            {
                sources.Append("; invalidFlag=").Append(token);
                continue;
            }

            flags = remove ? flags & ~parsed : flags | parsed;
        }

        sources.Append("; ")
            .Append(global::XREngine.XREngineEnvironmentVariables.VulkanDiagnosticFlags)
            .Append('=')
            .Append(raw.Trim());
    }

    private static void ApplyBooleanEnvOverride(
        string variableName,
        EVulkanDiagnosticFlags flag,
        ref EVulkanDiagnosticFlags flags,
        StringBuilder sources)
    {
        string? raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
            return;

        bool enabled = !IsFalseToken(raw);
        flags = enabled ? flags | flag : flags & ~flag;
        sources.Append("; ")
            .Append(variableName)
            .Append('=')
            .Append(enabled ? "on" : "off");
    }

    private static bool TryParsePreset(string raw, out EVulkanDiagnosticPreset preset)
    {
        string normalized = NormalizeName(raw);
        if (normalized.Equals("Validation", StringComparison.OrdinalIgnoreCase))
            normalized = nameof(EVulkanDiagnosticPreset.StandardValidation);
        else if (normalized.Equals("Sync", StringComparison.OrdinalIgnoreCase))
            normalized = nameof(EVulkanDiagnosticPreset.SyncValidation);
        else if (normalized.Equals("Gpu", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("GpuAssistedValidation", StringComparison.OrdinalIgnoreCase))
            normalized = nameof(EVulkanDiagnosticPreset.GpuAssisted);
        else if (normalized.Equals("RenderDoc", StringComparison.OrdinalIgnoreCase))
            normalized = nameof(EVulkanDiagnosticPreset.RenderDocFriendly);

        return Enum.TryParse(normalized, ignoreCase: true, out preset);
    }

    private static bool TryParseFlag(string raw, out EVulkanDiagnosticFlags flags)
    {
        string normalized = NormalizeName(raw);
        if (normalized.Equals("Validation", StringComparison.OrdinalIgnoreCase))
            normalized = nameof(EVulkanDiagnosticFlags.StandardValidation);
        else if (normalized.Equals("Sync", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("SyncValidation", StringComparison.OrdinalIgnoreCase))
            normalized = nameof(EVulkanDiagnosticFlags.SynchronizationValidation);
        else if (normalized.Equals("GpuAssisted", StringComparison.OrdinalIgnoreCase))
            normalized = nameof(EVulkanDiagnosticFlags.GpuAssistedValidation);
        else if (normalized.Equals("Crash", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("Breadcrumbs", StringComparison.OrdinalIgnoreCase))
            normalized = nameof(EVulkanDiagnosticFlags.CrashBreadcrumbs);
        else if (normalized.Equals("RenderDoc", StringComparison.OrdinalIgnoreCase))
            normalized = nameof(EVulkanDiagnosticFlags.RenderDocFriendly);
        else if (normalized.Equals("DeviceFaultLostOnMasked", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("LostOnMasked", StringComparison.OrdinalIgnoreCase))
            normalized = nameof(EVulkanDiagnosticFlags.DeviceFaultDeviceLostOnMasked);
        else if (normalized.Equals("Checkpoints", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("NvCheckpoints", StringComparison.OrdinalIgnoreCase))
            normalized = nameof(EVulkanDiagnosticFlags.NvDiagnosticCheckpoints);

        return Enum.TryParse(normalized, ignoreCase: true, out flags);
    }

    private static string NormalizeName(string raw)
    {
        ReadOnlySpan<char> source = raw.AsSpan().Trim();
        Span<char> buffer = stackalloc char[source.Length];
        int length = 0;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c is '-' or '_' or ' ')
                continue;

            buffer[length++] = c;
        }

        return new string(buffer[..length]);
    }

    private static bool IsFalseToken(string raw)
    {
        string value = raw.Trim();
        return value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildOverheadWarnings(EVulkanDiagnosticFlags flags)
    {
        StringBuilder builder = new();
        AppendWarningIf(
            builder,
            flags.HasFlag(EVulkanDiagnosticFlags.StandardValidation),
            "standard validation intercepts Vulkan calls");
        AppendWarningIf(
            builder,
            flags.HasFlag(EVulkanDiagnosticFlags.SynchronizationValidation),
            "sync validation is CPU-heavy and may change command-recording cost");
        AppendWarningIf(
            builder,
            flags.HasFlag(EVulkanDiagnosticFlags.GpuAssistedValidation),
            "GPU-assisted validation instruments shaders/descriptors and is not representative performance");
        AppendWarningIf(
            builder,
            flags.HasFlag(EVulkanDiagnosticFlags.CommandBufferLabels),
            "command-buffer labels marshal strings on command recording");
        AppendWarningIf(
            builder,
            flags.HasFlag(EVulkanDiagnosticFlags.RenderDocFriendly),
            "RenderDoc-friendly mode favors capture readability over minimum overhead");
        AppendWarningIf(
            builder,
            flags.HasFlag(EVulkanDiagnosticFlags.DeviceFaultDeviceLostOnMasked),
            "deviceFaultDeviceLostOnMasked may intentionally convert masked faults into device loss");

        return builder.ToString();
    }

    private static void AppendWarningIf(StringBuilder builder, bool condition, string message)
    {
        if (!condition)
            return;

        if (builder.Length > 0)
            builder.Append("; ");
        builder.Append(message);
    }
}
