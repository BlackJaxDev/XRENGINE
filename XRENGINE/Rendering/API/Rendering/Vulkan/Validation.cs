using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Core.Native;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private ExtDebugUtils? debugUtils;
    private DebugUtilsMessengerEXT debugMessenger;

#if DEBUG
    private bool EnableValidationLayers = true;
#else
    private bool EnableValidationLayers = false;
#endif

    private bool SupportsDebugUtilsLabels => debugUtils is not null;

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

        if (messageSeverity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt))
            Debug.LogError($"[Vulkan] {msg}");
        else if (messageSeverity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.WarningBitExt))
            Debug.LogWarning($"[Vulkan] {msg}");
        else
            Debug.Out($"[Vulkan] {msg}");

        return Vk.False;
    }
}