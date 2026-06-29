using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Core.Native;
using System.Runtime.InteropServices;
using System.Text;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private ExtDebugUtils? debugUtils;
    private DebugUtilsMessengerEXT debugMessenger;

    private bool EnableValidationLayers = ResolveValidationLayerDefault();

    private bool SupportsDebugUtilsLabels => debugUtils is not null;

    private static bool ResolveValidationLayerDefault()
    {
#if DEBUG
        const bool defaultValue = true;
#else
        const bool defaultValue = false;
#endif

        string? raw = Environment.GetEnvironmentVariable(global::XREngine.XREngineEnvironmentVariables.VulkanValidation);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        return !string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase);
    }

    private void CmdBeginLabel(CommandBuffer commandBuffer, string name)
    {
        if (!SupportsDebugUtilsLabels)
            return;

        nint namePtr = SilkMarshal.StringToPtr(name);
        try
        {
            DebugUtilsLabelEXT label = new()
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = (byte*)namePtr,
            };

            debugUtils!.CmdBeginDebugUtilsLabel(commandBuffer, in label);
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
        if (!SupportsDebugUtilsLabels || device.Handle == 0 || objectHandle == 0 || string.IsNullOrWhiteSpace(name))
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

    private readonly string[] validationLayers =
    [
        "VK_LAYER_KHRONOS_validation"
    ];

    private void DestroyValidationLayers()
    {
        if (EnableValidationLayers)
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
        if (!EnableValidationLayers)
            return;

        //TryGetInstanceExtension equivilant to method CreateDebugUtilsMessengerEXT from original tutorial.
        if (!Api!.TryGetInstanceExtension(instance, out debugUtils))
            return;

        DebugUtilsMessengerCreateInfoEXT createInfo = new();
        PopulateDebugMessengerCreateInfo(ref createInfo);

        if (debugUtils!.CreateDebugUtilsMessenger(instance, in createInfo, null, out debugMessenger) != Result.Success)
            throw new Exception("failed to set up debug messenger!");
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

        string objectSummary = FormatDebugCallbackObjects(pCallbackData);
        if (!string.IsNullOrEmpty(objectSummary))
            msg = $"{msg} objects=[{objectSummary}]";

        bool isError = messageSeverity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanValidationMessage(isError, msg);

        if (isError)
            Debug.VulkanError($"[Vulkan] {msg}");
        else if (messageSeverity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.WarningBitExt))
            Debug.VulkanWarning($"[Vulkan] {msg}");
        else
            Debug.Vulkan($"[Vulkan] {msg}");

        return Vk.False;
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
