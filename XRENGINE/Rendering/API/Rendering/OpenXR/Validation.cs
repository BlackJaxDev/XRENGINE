using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.EXT;
using System.Runtime.InteropServices;

public unsafe partial class OpenXRAPI
{
    private ExtDebugUtils? debugUtils;
    private DebugUtilsMessengerEXT debugMessenger;

    private bool EnableValidationLayers = true;

    private readonly string[] validationLayers = [];

    private void DestroyValidationLayers()
    {
        if (EnableValidationLayers)
            debugUtils!.DestroyDebugUtilsMessenger(debugMessenger);
    }

    private static void PopulateDebugMessengerCreateInfo(ref DebugUtilsMessengerCreateInfoEXT createInfo)
    {
        createInfo.Type = StructureType.DebugUtilsMessengerCreateInfoExt;
        createInfo.MessageSeverities =
            DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt |
            DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
            DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt;
        createInfo.MessageTypes = 
            DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
            DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt |
            DebugUtilsMessageTypeFlagsEXT.ValidationBitExt;
        createInfo.UserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback;
    }
    private void SetupDebugMessenger()
    {
        if (!EnableValidationLayers)
            return;

        if (Api!.TryGetInstanceExtension(null, _instance, out debugUtils))
            return;

        DebugUtilsMessengerCreateInfoEXT createInfo = new();
        PopulateDebugMessengerCreateInfo(ref createInfo);

        var d = new DebugUtilsMessengerEXT();
        if (debugUtils!.CreateDebugUtilsMessenger(_instance, &createInfo, &d) != Result.Success)
            throw new Exception("Failed to set up OpenXR debug messenger.");
        debugMessenger = d;
    }
    private bool CheckValidationLayerSupport()
    {
        uint layerCount = 0;
        Api!.EnumerateApiLayerProperties(ref layerCount, null);
        var availableLayers = new ApiLayerProperties[layerCount];
        fixed (ApiLayerProperties* availableLayersPtr = availableLayers)
        {
            Api!.EnumerateApiLayerProperties(layerCount, ref layerCount, availableLayersPtr);
        }

        var availableLayerNames = availableLayers.Select(layer => Marshal.PtrToStringAnsi((IntPtr)layer.LayerName)).ToHashSet();

        return validationLayers.All(availableLayerNames.Contains);
    }
    private static uint DebugCallback(DebugUtilsMessageSeverityFlagsEXT messageSeverity, DebugUtilsMessageTypeFlagsEXT messageTypes, DebugUtilsMessengerCallbackDataEXT* pCallbackData, void* pUserData)
    {
        Console.WriteLine($"validation layer:{Marshal.PtrToStringAnsi((nint)pCallbackData->Message)}");

        return XR.False;
    }
}