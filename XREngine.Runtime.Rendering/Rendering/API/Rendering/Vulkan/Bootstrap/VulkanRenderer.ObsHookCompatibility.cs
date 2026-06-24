using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

internal enum EVulkanObsHookPolicy
{
    Auto,
    Disable,
    Require,
}

public unsafe partial class VulkanRenderer
{
    private const string ObsHookLayerName = "VK_LAYER_OBS_HOOK";
    private const string ObsHookPolicyEnvVar = XREngineEnvironmentVariables.VkObsHook;
    private const string ObsHookDisableEnvVar = XREngineEnvironmentVariables.DisableVulkanObsCapture;
    private const string VulkanLoaderLayersDisableEnvVar = XREngineEnvironmentVariables.VulkanLoaderLayersDisable;
    private const string ObsRequiredDeviceExtension = "VK_KHR_external_memory_win32";

    private EVulkanObsHookPolicy _obsHookPolicy = EVulkanObsHookPolicy.Auto;
    private bool _obsHookLayerAvailable;
    private bool _obsHookDisabledForProcess;
    private bool _obsHookDisabledByLoader;
    private bool _obsHookDeviceCaptureReady;
    private string? _obsHookDeviceCaptureFailure;

    private bool ObsHookLayerLikelyEnabled
        => _obsHookLayerAvailable && !_obsHookDisabledForProcess && !_obsHookDisabledByLoader;

    private void PrepareObsHookCompatibility()
    {
        _obsHookPolicy = ResolveObsHookPolicy();

        if (_obsHookPolicy == EVulkanObsHookPolicy.Disable)
            Environment.SetEnvironmentVariable(ObsHookDisableEnvVar, "1");

        _obsHookDisabledForProcess = IsEnvironmentFlagEnabled(ObsHookDisableEnvVar);
        _obsHookDisabledByLoader = LoaderDisableEnvMentionsObsHook();
        _obsHookLayerAvailable = IsInstanceLayerAvailable(ObsHookLayerName);

        if (_obsHookPolicy == EVulkanObsHookPolicy.Require)
        {
            if (_obsHookDisabledForProcess)
            {
                throw new InvalidOperationException(
                    $"{ObsHookPolicyEnvVar}=Require was requested, but {ObsHookDisableEnvVar}=1 disables the OBS Vulkan hook layer.");
            }

            if (_obsHookDisabledByLoader)
            {
                throw new InvalidOperationException(
                    $"{ObsHookPolicyEnvVar}=Require was requested, but {VulkanLoaderLayersDisableEnvVar} appears to disable {ObsHookLayerName}.");
            }

            if (!_obsHookLayerAvailable)
            {
                throw new InvalidOperationException(
                    $"{ObsHookPolicyEnvVar}=Require was requested, but {ObsHookLayerName} is not reported by the Vulkan loader. Install OBS Studio with Vulkan game capture support or use {ObsHookPolicyEnvVar}=Auto.");
            }
        }

        if (_obsHookPolicy == EVulkanObsHookPolicy.Disable)
        {
            Debug.Vulkan(
                "[Vulkan][OBS] {0}=Disable set {1}=1 for this process; {2} will not hook this Vulkan instance.",
                ObsHookPolicyEnvVar,
                ObsHookDisableEnvVar,
                ObsHookLayerName);
            return;
        }

        if (_obsHookLayerAvailable)
        {
            Debug.Vulkan(
                "[Vulkan][OBS] {0} reported by the Vulkan loader (policy={1}, disabledByObsEnv={2}, disabledByLoaderEnv={3}). The engine leaves the implicit OBS layer enabled and will report device capture readiness after GPU selection.",
                ObsHookLayerName,
                _obsHookPolicy,
                _obsHookDisabledForProcess,
                _obsHookDisabledByLoader);
        }
        else
        {
            Debug.Vulkan(
                "[Vulkan][OBS] {0} is not reported by the Vulkan loader (policy={1}). OBS Game Capture for Vulkan requires the OBS implicit layer to be installed.",
                ObsHookLayerName,
                _obsHookPolicy);
        }
    }

    private void ValidateObsHookDeviceCompatibility(HashSet<string> availableExtensionSet, string[] enabledExtensions)
    {
        if (_obsHookPolicy == EVulkanObsHookPolicy.Auto && !ObsHookLayerLikelyEnabled)
            return;

        bool externalMemoryWin32Available = availableExtensionSet.Contains(ObsRequiredDeviceExtension);
        bool externalMemoryWin32Enabled = enabledExtensions.Contains(ObsRequiredDeviceExtension, StringComparer.Ordinal);
        bool sharedTextureImportSupported = false;
        Result sharedTextureImportResult = Result.ErrorExtensionNotPresent;
        ExternalMemoryFeatureFlags sharedTextureImportFeatures = 0;

        if (externalMemoryWin32Available)
        {
            sharedTextureImportSupported = TryQueryObsSharedTextureImportSupport(
                out sharedTextureImportFeatures,
                out sharedTextureImportResult);
        }

        _obsHookDeviceCaptureReady =
            ObsHookLayerLikelyEnabled &&
            externalMemoryWin32Available &&
            externalMemoryWin32Enabled &&
            sharedTextureImportSupported;

        _obsHookDeviceCaptureFailure = _obsHookDeviceCaptureReady
            ? null
            : BuildObsHookDeviceCaptureFailure(
                externalMemoryWin32Available,
                externalMemoryWin32Enabled,
                sharedTextureImportSupported,
                sharedTextureImportResult,
                sharedTextureImportFeatures);

        if (_obsHookPolicy == EVulkanObsHookPolicy.Require && !_obsHookDeviceCaptureReady)
        {
            throw new InvalidOperationException(
                $"{ObsHookPolicyEnvVar}=Require was requested, but Vulkan OBS capture is not ready: {_obsHookDeviceCaptureFailure}");
        }

        if (_obsHookDeviceCaptureReady)
        {
            Debug.Vulkan(
                "[Vulkan][OBS] Capture-ready device path confirmed: {0}=enabled, D3D11 texture KMT import result={1}, features={2}.",
                ObsRequiredDeviceExtension,
                sharedTextureImportResult,
                sharedTextureImportFeatures);
        }
        else if (_obsHookLayerAvailable || _obsHookPolicy == EVulkanObsHookPolicy.Require)
        {
            Debug.VulkanWarning(
                "[Vulkan][OBS] Capture device path is not ready: {0}",
                _obsHookDeviceCaptureFailure ?? "unknown failure");
        }
    }

    private bool TryQueryObsSharedTextureImportSupport(
        out ExternalMemoryFeatureFlags features,
        out Result result)
    {
        features = 0;

        PhysicalDeviceExternalImageFormatInfo externalInfo = new()
        {
            SType = StructureType.PhysicalDeviceExternalImageFormatInfo,
            PNext = null,
            HandleType = ExternalMemoryHandleTypeFlags.D3D11TextureKmtBit,
        };

        PhysicalDeviceImageFormatInfo2 formatInfo = new()
        {
            SType = StructureType.PhysicalDeviceImageFormatInfo2,
            PNext = &externalInfo,
            Format = Format.R8G8B8A8Unorm,
            Type = ImageType.Type2D,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            Flags = 0,
        };

        ExternalImageFormatProperties externalProperties = new()
        {
            SType = StructureType.ExternalImageFormatProperties,
            PNext = null,
        };

        ImageFormatProperties2 imageFormatProperties = new()
        {
            SType = StructureType.ImageFormatProperties2,
            PNext = &externalProperties,
        };

        result = Api!.GetPhysicalDeviceImageFormatProperties2(
            _physicalDevice,
            &formatInfo,
            &imageFormatProperties);

        if (result != Result.Success)
            return false;

        features = externalProperties.ExternalMemoryProperties.ExternalMemoryFeatures;
        return (features & ExternalMemoryFeatureFlags.ImportableBit) != 0;
    }

    private static string BuildObsHookDeviceCaptureFailure(
        bool externalMemoryWin32Available,
        bool externalMemoryWin32Enabled,
        bool sharedTextureImportSupported,
        Result sharedTextureImportResult,
        ExternalMemoryFeatureFlags sharedTextureImportFeatures)
    {
        if (!externalMemoryWin32Available)
            return $"{ObsRequiredDeviceExtension} is not reported by the selected Vulkan device.";

        if (!externalMemoryWin32Enabled)
            return $"{ObsRequiredDeviceExtension} is available but was not enabled.";

        if (!sharedTextureImportSupported)
        {
            return "D3D11 texture KMT import for OBS shared textures is unsupported " +
                $"(vkGetPhysicalDeviceImageFormatProperties2={sharedTextureImportResult}, externalMemoryFeatures={sharedTextureImportFeatures}).";
        }

        return $"{ObsHookLayerName} is not likely active for this process.";
    }

    private bool IsInstanceLayerAvailable(string layerName)
    {
        uint layerCount = 0;
        Result result = Api!.EnumerateInstanceLayerProperties(ref layerCount, null);
        if (result != Result.Success)
        {
            Debug.VulkanWarning(
                "[Vulkan][OBS] vkEnumerateInstanceLayerProperties failed while checking {0}: {1}",
                layerName,
                result);
            return false;
        }

        if (layerCount == 0)
            return false;

        var availableLayers = new LayerProperties[layerCount];
        fixed (LayerProperties* availableLayersPtr = availableLayers)
        {
            result = Api.EnumerateInstanceLayerProperties(ref layerCount, availableLayersPtr);

            if (result != Result.Success)
            {
                Debug.VulkanWarning(
                    "[Vulkan][OBS] vkEnumerateInstanceLayerProperties list query failed while checking {0}: {1}",
                    layerName,
                    result);
                return false;
            }

            for (uint i = 0; i < layerCount; i++)
            {
                string? availableLayerName = Marshal.PtrToStringAnsi((IntPtr)availableLayersPtr[i].LayerName);
                if (string.Equals(availableLayerName, layerName, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static EVulkanObsHookPolicy ResolveObsHookPolicy()
    {
        string? raw = Environment.GetEnvironmentVariable(ObsHookPolicyEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return EVulkanObsHookPolicy.Auto;

        string value = raw.Trim();
        if (StringEqualsAny(value, "auto", "default", "1", "true", "on", "enable", "enabled"))
            return EVulkanObsHookPolicy.Auto;

        if (StringEqualsAny(value, "disable", "disabled", "0", "false", "off"))
            return EVulkanObsHookPolicy.Disable;

        if (StringEqualsAny(value, "require", "required", "strict"))
            return EVulkanObsHookPolicy.Require;

        Debug.VulkanWarning(
            "[Vulkan][OBS] Unknown {0} value '{1}'. Expected Auto, Disable, or Require; defaulting to Auto.",
            ObsHookPolicyEnvVar,
            raw);
        return EVulkanObsHookPolicy.Auto;
    }

    private static bool LoaderDisableEnvMentionsObsHook()
    {
        string? raw = Environment.GetEnvironmentVariable(VulkanLoaderLayersDisableEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return raw.Contains(ObsHookLayerName, StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("OBS", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("~all~", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("~implicit~", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEnvironmentFlagEnabled(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(raw) &&
            !StringEqualsAny(raw.Trim(), "0", "false", "off", "no");
    }

    private static bool StringEqualsAny(string value, params string[] candidates)
    {
        for (int i = 0; i < candidates.Length; i++)
        {
            if (string.Equals(value, candidates[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
