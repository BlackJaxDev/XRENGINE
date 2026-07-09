using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Core.Native;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private const int MaxStructuredVulkanValidationMessages = 128;

    private ExtDebugUtils? debugUtils;
    private DebugUtilsMessengerEXT debugMessenger;

    private readonly VulkanDiagnosticOptions _diagnosticOptions = VulkanDiagnosticOptions.Resolve();
    private bool? _validationLayersEnabledOverride;
    private readonly object _vulkanValidationSummaryLock = new();
    private readonly Dictionary<string, VulkanValidationMessageAggregate> _vulkanValidationMessages = new(StringComparer.Ordinal);
    private int _vulkanValidationMessageOverflowCount;

    private bool EnableValidationLayers
    {
        get => _validationLayersEnabledOverride ?? _diagnosticOptions.EnableValidationLayers;
        set => _validationLayersEnabledOverride = value;
    }

    private bool SupportsDebugUtils => debugUtils is not null;
    private bool SupportsDebugUtilsLabels => SupportsDebugUtils && _diagnosticOptions.EnableCommandBufferLabels;
    private bool CanRecordCommandBufferDebugLabels => SupportsDebugUtilsLabels;

    private sealed class VulkanValidationMessageAggregate
    {
        public int Count;
        public int ErrorCount;
        public int WarningCount;
        public ulong FirstFrameId;
        public ulong LastFrameId;
        public string FirstSample = string.Empty;
        public string LastSample = string.Empty;
    }

    private bool CmdBeginLabel(CommandBuffer commandBuffer, string name)
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

            debugUtils!.CmdBeginDebugUtilsLabel(commandBuffer, in label);
            return true;
        }
        finally
        {
            SilkMarshal.Free(namePtr);
        }
    }

    private void CmdEndLabel(CommandBuffer commandBuffer)
    {
        if (!SupportsDebugUtilsLabels)
            return;

        debugUtils!.CmdEndDebugUtilsLabel(commandBuffer);
    }

    private void SetDebugObjectName(ObjectType objectType, ulong objectHandle, string name)
    {
        if (!SupportsDebugUtils || device.Handle == 0 || objectHandle == 0 || string.IsNullOrWhiteSpace(name))
            return;

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

            _ = debugUtils!.SetDebugUtilsObjectName(device, in nameInfo);
        }
        finally
        {
            SilkMarshal.Free(namePtr);
        }
    }

    private void SetDebugDescriptorSetName(DescriptorSet descriptorSet, string name)
        => SetDebugObjectName(ObjectType.DescriptorSet, descriptorSet.Handle, name);

    private void SetDebugDescriptorSetNames(DescriptorSet[]? sets, string prefix)
    {
        if (sets is null || sets.Length == 0)
            return;

        for (int i = 0; i < sets.Length; i++)
            SetDebugDescriptorSetName(sets[i], $"{prefix}[{i}]");
    }

    private readonly string[] validationLayers =
    [
        "VK_LAYER_KHRONOS_validation"
    ];

    private void DestroyValidationLayers()
    {
        if (EnableValidationLayers && debugUtils is not null)
            debugUtils!.DestroyDebugUtilsMessenger(instance, debugMessenger, null);
    }

    private void PopulateDebugMessengerCreateInfo(ref DebugUtilsMessengerCreateInfoEXT createInfo)
    {
        createInfo.SType = StructureType.DebugUtilsMessengerCreateInfoExt;
        createInfo.MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt |
                                     DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                                     DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt;
        createInfo.MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                                 DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt |
                                 DebugUtilsMessageTypeFlagsEXT.ValidationBitExt;
        createInfo.PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback;
    }
    private void SetupDebugMessenger()
    {
        if (!EnableValidationLayers && !_diagnosticOptions.EnableDebugUtils)
            return;

        //TryGetInstanceExtension equivilant to method CreateDebugUtilsMessengerEXT from original tutorial.
        if (!Api!.TryGetInstanceExtension(instance, out debugUtils))
            return;

        if (!EnableValidationLayers)
            return;

        DebugUtilsMessengerCreateInfoEXT createInfo = new();
        PopulateDebugMessengerCreateInfo(ref createInfo);

        if (debugUtils!.CreateDebugUtilsMessenger(instance, in createInfo, null, out debugMessenger) != Result.Success)
            throw new Exception("failed to set up debug messenger!");
    }

    private uint PopulateEnabledValidationFeatures(ValidationFeatureEnableEXT* enabledFeatures)
    {
        uint count = 0;
        if (_diagnosticOptions.EnableSynchronizationValidation)
            enabledFeatures[count++] = ValidationFeatureEnableEXT.SynchronizationValidationExt;

        if (_diagnosticOptions.EnableGpuAssistedValidation)
        {
            enabledFeatures[count++] = ValidationFeatureEnableEXT.GpuAssistedExt;
            enabledFeatures[count++] = ValidationFeatureEnableEXT.GpuAssistedReserveBindingSlotExt;
        }

        if (_diagnosticOptions.EnableBestPractices)
            enabledFeatures[count++] = ValidationFeatureEnableEXT.BestPracticesExt;

        return count;
    }

    private string DescribeEnabledValidationFeatures()
    {
        StringBuilder builder = new();
        if (_diagnosticOptions.EnableSynchronizationValidation)
            AppendCommaSeparated(builder, "SynchronizationValidation");
        if (_diagnosticOptions.EnableGpuAssistedValidation)
        {
            AppendCommaSeparated(builder, "GpuAssisted");
            AppendCommaSeparated(builder, "GpuAssistedReserveBindingSlot");
        }
        if (_diagnosticOptions.EnableBestPractices)
            AppendCommaSeparated(builder, "BestPractices");

        return builder.Length == 0 ? "<none>" : builder.ToString();
    }

    private void LogResolvedVulkanDiagnosticOptions(IReadOnlyList<string> instanceExtensions)
    {
        Debug.Vulkan(
            "[VulkanDiag] Preset={0} Flags={1} ValidationLayers={2} DebugUtils={3} Labels={4} Breadcrumbs={5} RenderDocFriendly={6} Source='{7}'",
            _diagnosticOptions.Preset,
            _diagnosticOptions.Flags,
            EnableValidationLayers,
            _diagnosticOptions.EnableDebugUtils,
            _diagnosticOptions.EnableCommandBufferLabels,
            _diagnosticOptions.EnableCrashBreadcrumbs,
            _diagnosticOptions.RenderDocFriendly,
            _diagnosticOptions.SourceSummary);

        if (!string.IsNullOrWhiteSpace(_diagnosticOptions.OverheadWarnings))
            Debug.VulkanWarning("[VulkanDiag] Overhead warnings: {0}", _diagnosticOptions.OverheadWarnings);

        Debug.Vulkan("[VulkanDiag] InstanceExtensions={0}", string.Join(",", instanceExtensions));
        Debug.Vulkan(
            "[VulkanDiag] ValidationLayer VK_LAYER_KHRONOS_validation: {0}",
            EnableValidationLayers ? "enabled" : "disabled: no validation diagnostic flag requested or layer unavailable");
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
        var availableLayers = new LayerProperties[layerCount];
        fixed (LayerProperties* availableLayersPtr = availableLayers)
        {
            Api!.EnumerateInstanceLayerProperties(ref layerCount, availableLayersPtr);
        }

        var availableLayerNames = availableLayers.Select(layer => Marshal.PtrToStringAnsi((IntPtr)layer.LayerName)).ToHashSet();

        return validationLayers.All(availableLayerNames.Contains);
    }
    private uint DebugCallback(DebugUtilsMessageSeverityFlagsEXT messageSeverity, DebugUtilsMessageTypeFlagsEXT messageTypes, DebugUtilsMessengerCallbackDataEXT* pCallbackData, void* pUserData)
    {
        string msg = Marshal.PtrToStringAnsi((nint)pCallbackData->PMessage) ?? "<null>";

        // Silently suppress harmless fragment-output-unused warnings that
        // occur when a multi-output shader (e.g. deferred G-buffer with 4
        // outputs) is bound against a render pass / dynamic rendering info
        // with fewer color attachments.  This happens intentionally when the
        // FBO target path (CreateFBOTargetCommands) draws OpaqueDeferred
        // objects to a single-attachment FBO such as SceneCaptureEnvColor.
        // The extra writes are safely discarded by the driver per the Vulkan
        // spec (§15.6); the validation layer warning is purely informational.
        if (messageSeverity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) &&
            msg.Contains("this write is unused") &&
            msg.Contains("pColorAttachments"))
        {
            // Swallow completely — do not log.
            return Vk.False;
        }

        RecordVulkanDeviceAddressBindingCallback(pCallbackData);

        string objectSummary = FormatDebugCallbackObjects(pCallbackData);
        if (!string.IsNullOrEmpty(objectSummary))
            msg = $"{msg} objects=[{objectSummary}]";

        bool isError = messageSeverity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt);
        RecordStructuredVulkanValidationMessage(messageSeverity, pCallbackData, msg);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanValidationMessage(isError, msg);

        if (isError)
            Debug.VulkanError($"[Vulkan] {msg}");
        else if (messageSeverity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.WarningBitExt))
            Debug.VulkanWarning($"[Vulkan] {msg}");
        else
            Debug.Vulkan($"[Vulkan] {msg}");

        return Vk.False;
    }

    private void RecordStructuredVulkanValidationMessage(
        DebugUtilsMessageSeverityFlagsEXT messageSeverity,
        DebugUtilsMessengerCallbackDataEXT* callbackData,
        string message)
    {
        if (callbackData is null)
            return;

        VulkanSubmissionDiagnosticContext submitContext = SnapshotLastVulkanSubmissionDiagnosticContext();
        string key = BuildStructuredVulkanValidationMessageKey(callbackData, message, submitContext.FrameOpKind);
        bool isError = messageSeverity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt);
        bool isWarning = messageSeverity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.WarningBitExt);

        lock (_vulkanValidationSummaryLock)
        {
            if (!_vulkanValidationMessages.TryGetValue(key, out VulkanValidationMessageAggregate? aggregate))
            {
                if (_vulkanValidationMessages.Count >= MaxStructuredVulkanValidationMessages)
                {
                    _vulkanValidationMessageOverflowCount++;
                    return;
                }

                aggregate = new()
                {
                    FirstFrameId = submitContext.FrameId,
                    FirstSample = message,
                };
                _vulkanValidationMessages.Add(key, aggregate);
            }

            aggregate.Count++;
            if (isError)
                aggregate.ErrorCount++;
            if (isWarning)
                aggregate.WarningCount++;
            aggregate.LastFrameId = submitContext.FrameId;
            aggregate.LastSample = message;
        }
    }

    private static string BuildStructuredVulkanValidationMessageKey(
        DebugUtilsMessengerCallbackDataEXT* callbackData,
        string message,
        string? frameOpKind)
    {
        string? vuid = ExtractVulkanValidationVuid(message);
        string? messageIdName = callbackData->PMessageIdName is null
            ? null
            : Marshal.PtrToStringAnsi((nint)callbackData->PMessageIdName);

        ulong firstObjectHandle = 0;
        ObjectType firstObjectType = ObjectType.Unknown;
        if (callbackData->ObjectCount > 0 && callbackData->PObjects is not null)
        {
            DebugUtilsObjectNameInfoEXT firstObject = callbackData->PObjects[0];
            firstObjectHandle = firstObject.ObjectHandle;
            firstObjectType = firstObject.ObjectType;
        }

        return $"{vuid ?? messageIdName ?? "<unknown>"}:0x{callbackData->MessageIdNumber:X}:frameOp={frameOpKind ?? "<unknown>"}:object={firstObjectType}/0x{firstObjectHandle:X}";
    }

    private static string? ExtractVulkanValidationVuid(string message)
    {
        const string prefix = "VUID-";
        int start = message.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return null;

        int end = start + prefix.Length;
        while (end < message.Length)
        {
            char c = message[end];
            if (char.IsWhiteSpace(c) || c is ':' or '\'' or '"' or ')' or '(')
                break;
            end++;
        }

        return message[start..end];
    }

    private string DescribeVulkanValidationSummary(int maxEntries = 6)
    {
        lock (_vulkanValidationSummaryLock)
        {
            if (_vulkanValidationMessages.Count == 0 && _vulkanValidationMessageOverflowCount == 0)
                return string.Empty;

            StringBuilder builder = new();
            builder.Append("ValidationSummary count=").Append(_vulkanValidationMessages.Count);
            if (_vulkanValidationMessageOverflowCount > 0)
                builder.Append(" overflow=").Append(_vulkanValidationMessageOverflowCount);

            int emitted = 0;
            foreach (KeyValuePair<string, VulkanValidationMessageAggregate> pair in _vulkanValidationMessages)
            {
                if (emitted >= maxEntries)
                    break;

                VulkanValidationMessageAggregate aggregate = pair.Value;
                builder
                    .Append(" [")
                    .Append(pair.Key)
                    .Append(" hits=")
                    .Append(aggregate.Count)
                    .Append(" errors=")
                    .Append(aggregate.ErrorCount)
                    .Append(" warnings=")
                    .Append(aggregate.WarningCount)
                    .Append(" frames=")
                    .Append(aggregate.FirstFrameId)
                    .Append('-')
                    .Append(aggregate.LastFrameId)
                    .Append(']');
                emitted++;
            }

            return builder.ToString();
        }
    }

    private static string FormatDebugCallbackObjects(DebugUtilsMessengerCallbackDataEXT* callbackData)
    {
        if (callbackData is null ||
            callbackData->ObjectCount == 0 ||
            callbackData->PObjects is null)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        DebugUtilsObjectNameInfoEXT* objects = callbackData->PObjects;
        uint objectCount = callbackData->ObjectCount;
        for (uint i = 0; i < objectCount; i++)
        {
            DebugUtilsObjectNameInfoEXT info = objects[i];
            if (builder.Length > 0)
                builder.Append("; ");

            string? objectName = info.PObjectName is null
                ? null
                : Marshal.PtrToStringAnsi((nint)info.PObjectName);

            builder
                .Append(info.ObjectType)
                .Append(" 0x")
                .Append(info.ObjectHandle.ToString("X"));

            if (!string.IsNullOrWhiteSpace(objectName))
                builder.Append(" '").Append(objectName).Append('\'');
        }

        return builder.ToString();
    }
}
