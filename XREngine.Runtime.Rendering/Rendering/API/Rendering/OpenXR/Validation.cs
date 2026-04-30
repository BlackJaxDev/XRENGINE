using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.EXT;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _debugMessenger;

    private bool _enableValidationLayers = true;
    public bool EnableValidationLayers
    {
        get => _enableValidationLayers;
        set
        {
            if (_enableValidationLayers != value)
            {
                _enableValidationLayers = value;
                DestroyValidationLayers();
                SetupDebugMessenger();
            }
        }
    }

    private readonly string[] _validationLayers = [];

    private void DestroyValidationLayers()
    {
        if (_enableValidationLayers)
            _debugUtils?.DestroyDebugUtilsMessenger(_debugMessenger);
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

        if (!Api!.TryGetInstanceExtension(null, _instance, out _debugUtils) || _debugUtils is null)
            return;

        DebugUtilsMessengerCreateInfoEXT createInfo = new();
        PopulateDebugMessengerCreateInfo(ref createInfo);

        var d = new DebugUtilsMessengerEXT();
        if (_debugUtils!.CreateDebugUtilsMessenger(_instance, &createInfo, &d) != Result.Success)
            throw new Exception("Failed to set up OpenXR debug messenger.");
        _debugMessenger = d;
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

        return _validationLayers.All(availableLayerNames.Contains);
    }
    private static uint DebugCallback(DebugUtilsMessageSeverityFlagsEXT messageSeverity, DebugUtilsMessageTypeFlagsEXT messageTypes, DebugUtilsMessengerCallbackDataEXT* pCallbackData, void* pUserData)
    {
        Console.WriteLine($"validation layer:{Marshal.PtrToStringAnsi((nint)pCallbackData->Message)}");

        return XR.False;
    }
}