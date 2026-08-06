using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using System.Runtime.InteropServices;
using System.Text;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{

    private bool EnableValidationLayers
    {
        get => _deviceContext.MutableCapabilities._validationLayersEnabledOverride ?? _frameTelemetry._diagnosticOptions.EnableValidationLayers;
        set => _deviceContext.MutableCapabilities._validationLayersEnabledOverride = value;
    }

    private bool SupportsDebugUtils => _deviceContext.DebugUtils is not null;
    private bool SupportsDebugUtilsLabels =>
        SupportsDebugUtils && _frameTelemetry._diagnosticOptions.EnableCommandBufferLabels;
    internal bool CanRecordCommandBufferDebugLabels => SupportsDebugUtilsLabels;

    internal bool CmdBeginLabel(CommandBuffer commandBuffer, string name)
    {
        if (!SupportsDebugUtilsLabels)
            return false;

        nint namePtr = SilkMarshal.StringToPtr(name);
        try
        {
            DebugUtilsLabelEXT label = new()
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = (byte*)namePtr,
            };
            _deviceContext.DebugUtils!.CmdBeginDebugUtilsLabel(commandBuffer, in label);
            return true;
        }
        finally
        {
            SilkMarshal.Free(namePtr);
        }
    }

    internal void CmdEndLabel(CommandBuffer commandBuffer)
    {
        if (SupportsDebugUtilsLabels)
            _deviceContext.DebugUtils!.CmdEndDebugUtilsLabel(commandBuffer);
    }

    internal void SetDebugObjectName(ObjectType objectType, ulong objectHandle, string name)
    {
        if (!SupportsDebugUtils ||
            _deviceContext.Device.Handle == 0 ||
            objectHandle == 0 ||
            string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        nint namePtr = SilkMarshal.StringToPtr(name);
        try
        {
            DebugUtilsObjectNameInfoEXT nameInfo = new()
            {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = objectType,
                ObjectHandle = objectHandle,
                PObjectName = (byte*)namePtr,
            };
            _ = _deviceContext.DebugUtils!.SetDebugUtilsObjectName(_deviceContext.Device, in nameInfo);
        }
        finally
        {
            SilkMarshal.Free(namePtr);
        }
    }

    internal void SetDebugDescriptorSetName(DescriptorSet descriptorSet, string name)
        => SetDebugObjectName(ObjectType.DescriptorSet, descriptorSet.Handle, name);

    internal void SetDebugDescriptorSetNames(DescriptorSet[]? sets, string prefix)
    {
        if (sets is null || sets.Length == 0)
            return;
        for (int i = 0; i < sets.Length; i++)
            SetDebugDescriptorSetName(sets[i], $"{prefix}[{i}]");
    }


    private void SetupDebugMessenger()
        => _deviceContext.SetupDebugMessenger(
            Api!,
            EnableValidationLayers,
            _frameTelemetry._diagnosticOptions.EnableDebugUtils);

    private uint PopulateEnabledValidationFeatures(
        ValidationFeatureEnableEXT* enabledFeatures)
    {
        uint count = 0;
        if (_frameTelemetry._diagnosticOptions.EnableSynchronizationValidation)
            enabledFeatures[count++] = ValidationFeatureEnableEXT.SynchronizationValidationExt;
        if (_frameTelemetry._diagnosticOptions.EnableGpuAssistedValidation)
        {
            enabledFeatures[count++] = ValidationFeatureEnableEXT.GpuAssistedExt;
            enabledFeatures[count++] = ValidationFeatureEnableEXT.GpuAssistedReserveBindingSlotExt;
        }
        if (_frameTelemetry._diagnosticOptions.EnableBestPractices)
            enabledFeatures[count++] = ValidationFeatureEnableEXT.BestPracticesExt;
        return count;
    }

    private string DescribeEnabledValidationFeatures()
    {
        StringBuilder builder = new();
        if (_frameTelemetry._diagnosticOptions.EnableSynchronizationValidation)
            AppendCommaSeparated(builder, "SynchronizationValidation");
        if (_frameTelemetry._diagnosticOptions.EnableGpuAssistedValidation)
        {
            AppendCommaSeparated(builder, "GpuAssisted");
            AppendCommaSeparated(builder, "GpuAssistedReserveBindingSlot");
        }
        if (_frameTelemetry._diagnosticOptions.EnableBestPractices)
            AppendCommaSeparated(builder, "BestPractices");
        return builder.Length == 0 ? "<none>" : builder.ToString();
    }

    private void LogResolvedVulkanDiagnosticOptions(
        IReadOnlyList<string> instanceExtensions)
    {
        Debug.Vulkan(
            "[VulkanDiag] Preset={0} Flags={1} ValidationLayers={2} DebugUtils={3} Labels={4} Breadcrumbs={5} RenderDocFriendly={6} Source='{7}'",
            _frameTelemetry._diagnosticOptions.Preset,
            _frameTelemetry._diagnosticOptions.Flags,
            EnableValidationLayers,
            _frameTelemetry._diagnosticOptions.EnableDebugUtils,
            _frameTelemetry._diagnosticOptions.EnableCommandBufferLabels,
            _frameTelemetry._diagnosticOptions.EnableCrashBreadcrumbs,
            _frameTelemetry._diagnosticOptions.RenderDocFriendly,
            _frameTelemetry._diagnosticOptions.SourceSummary);
        if (!string.IsNullOrWhiteSpace(_frameTelemetry._diagnosticOptions.OverheadWarnings))
        {
            Debug.VulkanWarning(
                "[VulkanDiag] Overhead warnings: {0}",
                _frameTelemetry._diagnosticOptions.OverheadWarnings);
        }

        Debug.Vulkan("[VulkanDiag] InstanceExtensions={0}", string.Join(",", instanceExtensions));
        Debug.Vulkan(
            "[VulkanDiag] ValidationLayer VK_LAYER_KHRONOS_validation: {0}",
            EnableValidationLayers
                ? "enabled"
                : "disabled: no validation diagnostic flag requested or layer unavailable");
        Debug.Vulkan("[VulkanDiag] ValidationFeatures={0}", DescribeEnabledValidationFeatures());
    }

    private static void AppendCommaSeparated(StringBuilder builder, string value)
    {
        if (builder.Length > 0)
            builder.Append(',');
        builder.Append(value);
    }

    private bool CheckValidationLayerSupport()
    {
        uint layerCount = 0;
        Api!.EnumerateInstanceLayerProperties(ref layerCount, null);
        LayerProperties[] availableLayers = new LayerProperties[layerCount];
        fixed (LayerProperties* availableLayersPtr = availableLayers)
            Api.EnumerateInstanceLayerProperties(ref layerCount, availableLayersPtr);

        HashSet<string?> availableLayerNames = availableLayers
            .Select(static layer => Marshal.PtrToStringAnsi((nint)layer.LayerName))
            .ToHashSet(StringComparer.Ordinal);
        return _deviceContext.MutableCapabilities.validationLayers.All(availableLayerNames.Contains);
    }

    private string DescribeVulkanValidationSummary(int maxEntries = 6)
        => _deviceContext.ValidationDiagnostics.Describe(maxEntries);
}
