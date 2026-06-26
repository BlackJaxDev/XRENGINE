using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.EXT;
using System.Runtime.InteropServices;
using System.Threading;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _debugMessenger;
    private int _debugUtilsUnavailableLogged;

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
        if (_debugMessenger.Handle != 0 && _debugUtils is not null)
        {
            try
            {
                _debugUtils.DestroyDebugUtilsMessenger(_debugMessenger);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"OpenXR debug utils messenger destroy failed: {ex.Message}");
            }
        }

        _debugMessenger = default;
        _debugUtils = null;
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
        if (!EnableValidationLayers || _instance.Handle == 0)
            return;

        DestroyValidationLayers();

        if (!IsInstanceExtensionEnabled(ExtDebugUtils.ExtensionName))
        {
            LogDebugUtilsUnavailable(DescribeInstanceExtensionState(ExtDebugUtils.ExtensionName));
            return;
        }

        try
        {
            if (!Api!.TryGetInstanceExtension(null, _instance, out _debugUtils) || _debugUtils is null)
            {
                LogDebugUtilsUnavailable("Silk.NET did not return the extension wrapper");
                return;
            }
        }
        catch (Exception ex)
        {
            LogDebugUtilsUnavailable($"extension load failed: {ex.Message}");
            _debugUtils = null;
            return;
        }

        DebugUtilsMessengerCreateInfoEXT createInfo = new();
        PopulateDebugMessengerCreateInfo(ref createInfo);

        var d = new DebugUtilsMessengerEXT();
        Result result;
        try
        {
            result = _debugUtils!.CreateDebugUtilsMessenger(_instance, &createInfo, &d);
        }
        catch (Exception ex)
        {
            LogDebugUtilsUnavailable($"messenger create failed: {ex.Message}");
            _debugUtils = null;
            return;
        }

        if (result != Result.Success)
        {
            LogDebugUtilsUnavailable($"messenger create returned {result}");
            _debugUtils = null;
            return;
        }

        _debugMessenger = d;
    }

    private void LogDebugUtilsUnavailable(string reason)
    {
        if (Interlocked.Exchange(ref _debugUtilsUnavailableLogged, 1) != 0)
            return;

        Debug.LogWarning(
            "OpenXR debug utils messenger is unavailable; validation messenger disabled. " +
            $"Reason={reason}");
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
        string message = Marshal.PtrToStringAnsi((nint)pCallbackData->Message) ?? string.Empty;
        if ((messageSeverity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0 ||
            (messageSeverity & DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) != 0)
        {
            Debug.LogWarning($"OpenXR validation: {message}");
        }
        else
        {
            Debug.Out($"OpenXR validation: {message}");
        }

        return XR.False;
    }
}
