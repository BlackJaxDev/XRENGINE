using System.Reflection;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private string[] _streamlineRequiredInstanceExtensions = [];
    private string[] _streamlineRequiredDeviceExtensions = [];
    private string[] _streamlineRequiredFeatures12 = [];
    private string[] _streamlineRequiredFeatures13 = [];
    private NvidiaDlssManager.Native.StreamlineQueueRequirements _streamlineQueueRequirements;
    private uint _streamlineMinimumApiVersion = Vk.Version11;
    private uint _streamlineGraphicsQueueIndex;
    private uint _streamlineGraphicsQueueFamily;
    private uint _streamlineComputeQueueIndex;
    private uint _streamlineComputeQueueFamily;
    private uint _streamlineOpticalFlowQueueIndex;
    private uint _streamlineOpticalFlowQueueFamily;
    private bool _streamlineDlssProvisioned;
    private bool _streamlineFrameGenerationProvisioned;

    internal uint StreamlineGraphicsQueueIndex => _streamlineGraphicsQueueIndex;
    internal uint StreamlineGraphicsQueueFamily => _streamlineGraphicsQueueFamily;
    internal uint StreamlineComputeQueueIndex => _streamlineComputeQueueIndex;
    internal uint StreamlineComputeQueueFamily => _streamlineComputeQueueFamily;
    internal uint StreamlineOpticalFlowQueueIndex => _streamlineOpticalFlowQueueIndex;
    internal uint StreamlineOpticalFlowQueueFamily => _streamlineOpticalFlowQueueFamily;
    internal bool StreamlineUsesNativeOpticalFlow => _streamlineQueueRequirements.OpticalFlowQueues > 0;
    internal bool StreamlineDlssProvisioned => _streamlineDlssProvisioned;
    internal bool StreamlineFrameGenerationProvisioned => _streamlineFrameGenerationProvisioned;
    private bool IsStreamlineFrameGenerationRequested
        => _streamlineFrameGenerationProvisioned && NvidiaDlssManager.IsFrameGenerationRequested;

    /// <summary>
    /// Resolves all Vulkan instance, device, feature, and queue requirements before Vulkan objects are created.
    /// Streamline manual hooking requires this query for every enabled feature.
    /// </summary>
    private void PrepareStreamlineVulkanRequirements()
    {
        if (XRWindow.IsSecondaryGpuContext)
        {
            Debug.Rendering("[Vulkan] Streamline disabled for the secondary GPU compute context; the process-global runtime remains owned by the presentation renderer.");
            return;
        }

        bool dlssRequested = RuntimeEngine.EffectiveSettings.EnableNvidiaDlss
            || RuntimeEngine.EffectiveSettings.AntiAliasingMode == EAntiAliasingMode.Dlaa;
        bool frameGenerationRequested = NvidiaDlssManager.IsFrameGenerationRequested;
        // RenderDoc and Streamline both interpose Vulkan entry points. Provisioning a
        // proxy swapchain solely for a future editor toggle makes an otherwise plain
        // RenderDoc capture fail during startup even when DLSS and DLSS-G are off.
        // Explicit requests remain authoritative below and still surface any
        // incompatibility instead of silently falling back.
        bool provisionRuntimeToggles = !_diagnosticOptions.RenderDocFriendly
            && ShouldProvisionStreamlineRuntimeToggles();
        if (_diagnosticOptions.RenderDocFriendly
            && !dlssRequested
            && !frameGenerationRequested
            && (NvidiaDlssManager.RequiredRuntimeDllsAvailable
                || NvidiaDlssManager.FrameGenerationRuntimeDllsAvailable))
        {
            Debug.Rendering(
                "[Vulkan] RenderDoc-friendly diagnostics skipped optional Streamline runtime-toggle provisioning. Explicit DLSS/DLSS-G requests remain strict.");
        }

        bool frameGenerationRuntimeAvailable = NvidiaDlssManager.FrameGenerationRuntimeDllsAvailable;
        if (frameGenerationRequested && !frameGenerationRuntimeAvailable)
            throw new InvalidOperationException(NvidiaDlssManager.FrameGenerationRuntimeDllsUnavailableReason);

        bool frameGenerationSupported = false;
        string frameGenerationUnavailableReason = string.Empty;
        if ((frameGenerationRequested || provisionRuntimeToggles) && frameGenerationRuntimeAvailable)
        {
            frameGenerationSupported = NvidiaDlssManager.Native.TryCheckFrameGenerationSupport(
                vulkanPhysicalDevice: 0,
                out frameGenerationUnavailableReason);
        }

        if (frameGenerationRequested && !frameGenerationSupported)
        {
            throw new InvalidOperationException(
                $"Requested NVIDIA DLSS frame generation is unsupported before Vulkan instance creation: {frameGenerationUnavailableReason}");
        }

        if (!frameGenerationRequested
            && provisionRuntimeToggles
            && frameGenerationRuntimeAvailable
            && !frameGenerationSupported)
        {
            Debug.RenderingWarning(
                "[Vulkan] Optional DLSS-G runtime-toggle provisioning skipped because no supported adapter was found. Reason={0}",
                frameGenerationUnavailableReason);
        }

        bool includeDlss = dlssRequested
            || (provisionRuntimeToggles && NvidiaDlssManager.RequiredRuntimeDllsAvailable);
        bool includeFrameGeneration = frameGenerationRequested
            || ShouldProvisionOptionalStreamlineFrameGeneration(
                provisionRuntimeToggles,
                frameGenerationRuntimeAvailable,
                frameGenerationSupported);
        if (!includeDlss && !includeFrameGeneration)
            return;

        if (dlssRequested && !NvidiaDlssManager.RequiredRuntimeDllsAvailable)
            throw new InvalidOperationException(NvidiaDlssManager.RequiredRuntimeDllsUnavailableReason);

        ResolveStreamlineVulkanRequirements(includeDlss, includeFrameGeneration);
    }

    /// <summary>
    /// Rechecks DLSS-G against the physical device selected by Vulkan. General support can
    /// shrink after adapter selection, so optional editor-toggle provisioning must be removed
    /// rather than turning an unrequested feature into a startup failure.
    /// </summary>
    private void ValidateStreamlineSelectedPhysicalDevice()
    {
        if (!_streamlineFrameGenerationProvisioned)
            return;

        if (NvidiaDlssManager.Native.TryCheckFrameGenerationSupport(
                _physicalDevice.Handle,
                out string failureReason))
        {
            return;
        }

        if (NvidiaDlssManager.IsFrameGenerationRequested)
        {
            throw new InvalidOperationException(
                $"Requested NVIDIA DLSS frame generation is unsupported on the selected Vulkan physical device: {failureReason}");
        }

        Debug.RenderingWarning(
            "[Vulkan] Optional DLSS-G runtime-toggle provisioning disabled for the selected physical device. Reason={0}",
            failureReason);
        ResolveStreamlineVulkanRequirements(_streamlineDlssProvisioned, includeFrameGeneration: false);
    }

    private void ResolveStreamlineVulkanRequirements(bool includeDlss, bool includeFrameGeneration)
    {
        _streamlineDlssProvisioned = includeDlss;
        _streamlineFrameGenerationProvisioned = includeFrameGeneration;

        if (!includeDlss && !includeFrameGeneration)
        {
            _streamlineRequiredInstanceExtensions = [];
            _streamlineRequiredDeviceExtensions = [];
            _streamlineRequiredFeatures12 = [];
            _streamlineRequiredFeatures13 = [];
            _streamlineQueueRequirements = default;
            _streamlineMinimumApiVersion = Vk.Version11;
            return;
        }

        if (!NvidiaDlssManager.Native.TryGetRequiredVulkanRequirements(
                includeDlss,
                includeFrameGeneration,
                out _streamlineRequiredInstanceExtensions,
                out _streamlineRequiredDeviceExtensions,
                out _streamlineRequiredFeatures12,
                out _streamlineRequiredFeatures13,
                out _streamlineQueueRequirements,
                out string failureReason))
        {
            throw new InvalidOperationException(
                $"Requested NVIDIA DLSS Vulkan requirements could not be resolved before device creation: {failureReason}");
        }

        _streamlineMinimumApiVersion = _streamlineRequiredFeatures13.Length > 0
            ? Vk.Version13
            : _streamlineRequiredFeatures12.Length > 0
                ? Vk.Version12
                : Vk.Version11;

        Debug.Rendering(
            "[Vulkan] Streamline requirements prepared. DLSS={0} DLSS-G={1} InstanceExtensions=[{2}] DeviceExtensions=[{3}] Features12=[{4}] Features13=[{5}] ExtraQueues=G{6}/C{7}/OF{8}",
            includeDlss,
            includeFrameGeneration,
            string.Join(",", _streamlineRequiredInstanceExtensions),
            string.Join(",", _streamlineRequiredDeviceExtensions),
            string.Join(",", _streamlineRequiredFeatures12),
            string.Join(",", _streamlineRequiredFeatures13),
            _streamlineQueueRequirements.GraphicsQueues,
            _streamlineQueueRequirements.ComputeQueues,
            _streamlineQueueRequirements.OpticalFlowQueues);
    }

    internal static bool ShouldProvisionOptionalStreamlineFrameGeneration(
        bool provisionRuntimeToggles,
        bool runtimeDllsAvailable,
        bool featureSupported)
        => provisionRuntimeToggles
            && runtimeDllsAvailable
            && featureSupported;

    /// <summary>
    /// Streamline's Vulkan extensions, features, and queues cannot be added after device creation.
    /// Probe before creating the renderer so editor toggles remain live on NVIDIA Vulkan systems,
    /// while other vendors are not forced to satisfy NVIDIA-only device requirements.
    /// </summary>
    private static bool ShouldProvisionStreamlineRuntimeToggles()
    {
        if (!NvidiaDlssManager.RequiredRuntimeDllsAvailable &&
            !NvidiaDlssManager.FrameGenerationRuntimeDllsAvailable)
        {
            return false;
        }

        // Supplying the vendor hint matters on hybrid systems: the generic bridge probe
        // may otherwise choose an integrated adapter even though the presentation renderer
        // and the DLSS-capable discrete adapter are NVIDIA.
        VulkanUpscaleBridgeProbeResult probe = VulkanUpscaleBridgeProbe.Probe("NVIDIA", null);
        const uint nvidiaVendorId = 0x10DE;
        return probe.ProbeSucceeded && probe.SelectedVendorId == nvidiaVendorId;
    }

    private bool HasStreamlineVulkanRequirements
        => _streamlineRequiredInstanceExtensions.Length > 0
            || _streamlineRequiredDeviceExtensions.Length > 0
            || _streamlineRequiredFeatures12.Length > 0
            || _streamlineRequiredFeatures13.Length > 0
            || _streamlineQueueRequirements.GraphicsQueues > 0
            || _streamlineQueueRequirements.ComputeQueues > 0
            || _streamlineQueueRequirements.OpticalFlowQueues > 0;

    private void PopulateStreamlineRequiredFeatures<TFeatures>(
        ref TFeatures requestedFeatures,
        in TFeatures supportedFeatures,
        string[] featureNames,
        string featureGroup) where TFeatures : struct
    {
        List<string> unknownFeatures = [];
        List<string> unsupportedFeatures = [];

        foreach (string featureName in featureNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal))
        {
            string fieldName = char.ToUpperInvariant(featureName[0]) + featureName[1..];
            FieldInfo? field = typeof(TFeatures).GetField(fieldName, BindingFlags.Public | BindingFlags.Instance)
                ?? typeof(TFeatures).GetField(featureName, BindingFlags.Public | BindingFlags.Instance);
            if (field is null)
            {
                unknownFeatures.Add(featureName);
                continue;
            }

            object boxedSupported = supportedFeatures;
            if (!TryReadBooleanFeatureValue(field.GetValue(boxedSupported), out bool supported) || !supported)
            {
                unsupportedFeatures.Add(featureName);
                continue;
            }

            object boxedRequested = requestedFeatures;
            field.SetValue(boxedRequested, CreateBooleanFeatureValue(field.FieldType));
            requestedFeatures = (TFeatures)boxedRequested;
        }

        if (unknownFeatures.Count > 0)
            throw new InvalidOperationException($"Streamline requested unknown {featureGroup} feature fields: {string.Join(", ", unknownFeatures)}.");

        if (unsupportedFeatures.Count > 0)
            throw new NotSupportedException($"The selected Vulkan device does not support Streamline-required {featureGroup} features: {string.Join(", ", unsupportedFeatures)}.");
    }

    private static bool TryReadBooleanFeatureValue(object? rawValue, out bool value)
    {
        switch (rawValue)
        {
            case bool boolValue:
                value = boolValue;
                return true;
            case uint uintValue:
                value = uintValue != 0;
                return true;
            case int intValue:
                value = intValue != 0;
                return true;
            case byte byteValue:
                value = byteValue != 0;
                return true;
            case Bool32 bool32Value:
                value = bool32Value;
                return true;
            case null:
                value = false;
                return false;
        }

        Type valueType = rawValue.GetType();
        object? nestedValue = valueType.GetField("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(rawValue)
            ?? valueType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(rawValue);
        if (nestedValue is not null)
            return TryReadBooleanFeatureValue(nestedValue, out value);

        value = false;
        return false;
    }

    private static object CreateBooleanFeatureValue(Type fieldType)
    {
        if (fieldType == typeof(bool))
            return true;
        if (fieldType == typeof(uint))
            return 1u;
        if (fieldType == typeof(int))
            return 1;
        if (fieldType == typeof(byte))
            return (byte)1;
        if (fieldType == typeof(Bool32))
            return new Bool32(true);

        object instance = Activator.CreateInstance(fieldType)
            ?? throw new InvalidOperationException($"Could not construct Vulkan feature field type '{fieldType.FullName}'.");
        FieldInfo? valueField = fieldType.GetField("Value", BindingFlags.Public | BindingFlags.Instance);
        if (valueField is not null)
        {
            valueField.SetValue(instance, CreateBooleanFeatureValue(valueField.FieldType));
            return instance;
        }

        PropertyInfo? valueProperty = fieldType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (valueProperty?.CanWrite == true)
        {
            valueProperty.SetValue(instance, CreateBooleanFeatureValue(valueProperty.PropertyType));
            return instance;
        }

        throw new InvalidOperationException(
            $"Unsupported Vulkan feature field type '{fieldType.FullName}' in Streamline requirement translation.");
    }

    private static uint AppendRequiredQueues(
        Dictionary<uint, uint> requestedQueueCounts,
        QueueFamilyProperties[] queueFamilies,
        uint familyIndex,
        uint additionalCount,
        string queueKind)
    {
        uint firstIndex = requestedQueueCounts.GetValueOrDefault(familyIndex);
        if (additionalCount == 0)
            return firstIndex;

        uint requestedCount = checked(firstIndex + additionalCount);
        uint availableCount = queueFamilies[familyIndex].QueueCount;
        if (requestedCount > availableCount)
        {
            throw new NotSupportedException(
                $"Streamline requires {additionalCount} additional {queueKind} queue(s) beginning at index {firstIndex} in Vulkan queue family {familyIndex}, " +
                $"but that family exposes only {availableCount} queue(s). Runtime toggling requires recreating the renderer on a compatible device.");
        }

        requestedQueueCounts[familyIndex] = requestedCount;
        return firstIndex;
    }

    private static uint FindOpticalFlowQueueFamily(QueueFamilyProperties[] queueFamilies)
    {
        const QueueFlags opticalFlowBitNv = (QueueFlags)0x00000100;
        for (uint index = 0; index < queueFamilies.Length; index++)
        {
            if ((queueFamilies[index].QueueFlags & opticalFlowBitNv) != 0)
                return index;
        }

        throw new NotSupportedException(
            "Streamline DLSS-G requested a native Vulkan optical-flow queue, but the selected device exposes no VK_QUEUE_OPTICAL_FLOW_BIT_NV queue family.");
    }
}
