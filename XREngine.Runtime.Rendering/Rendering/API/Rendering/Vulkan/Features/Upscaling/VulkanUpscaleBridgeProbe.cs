using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanUpscaleBridgeProbeResult
{
    public bool ProbeSucceeded { get; init; }
    public bool HasVulkanExternalMemoryImport { get; init; }
    public bool HasVulkanExternalSemaphoreImport { get; init; }
    public string? SelectedDeviceName { get; init; }
    public uint SelectedVendorId { get; init; }
    public uint SelectedDeviceId { get; init; }
    public bool? SamePhysicalGpu { get; init; }
    public string? GpuIdentityReason { get; init; }
    public string? ProbeFailureReason { get; init; }
}

internal static unsafe class VulkanUpscaleBridgeProbe
{
    private const uint NvidiaVendorId = 0x10DE;
    private const uint IntelVendorId = 0x8086;
    private const uint AmdVendorId = 0x1002;

    private static readonly string[] RequiredBridgeDeviceExtensions =
    [
        "VK_KHR_external_memory",
        "VK_KHR_external_semaphore",
        "VK_KHR_external_memory_win32",
        "VK_KHR_external_semaphore_win32",
    ];

    private sealed class Candidate
    {
        public PhysicalDevice Device { get; init; }
        public PhysicalDeviceProperties Properties { get; init; }
        public required HashSet<string> ExtensionNames { get; init; }
        public string DeviceName { get; init; } = string.Empty;
        public uint VendorId { get; init; }
        public uint DeviceId { get; init; }
        public uint? GraphicsQueueFamilyIndex { get; init; }
        public bool HasExternalMemory { get; init; }
        public bool HasExternalSemaphore { get; init; }
        public bool HasExternalMemoryWin32 { get; init; }
        public bool HasExternalSemaphoreWin32 { get; init; }
        public bool RendererMatches { get; init; }
        public bool VendorMatches { get; init; }

        public bool SupportsBridgeImport
            => HasExternalMemory && HasExternalSemaphore && HasExternalMemoryWin32 && HasExternalSemaphoreWin32;
    }

    internal sealed class VulkanUpscaleBridgeSelectedDevice
    {
        public PhysicalDevice Device { get; init; }
        public PhysicalDeviceProperties Properties { get; init; }
        public required HashSet<string> ExtensionNames { get; init; }
        public string DeviceName { get; init; } = string.Empty;
        public uint VendorId { get; init; }
        public uint DeviceId { get; init; }
        public uint GraphicsQueueFamilyIndex { get; init; }
        public bool HasExternalMemory { get; init; }
        public bool HasExternalSemaphore { get; init; }
        public bool HasExternalMemoryWin32 { get; init; }
        public bool HasExternalSemaphoreWin32 { get; init; }
        public bool? SamePhysicalGpu { get; init; }
        public string? GpuIdentityReason { get; init; }
        public bool SupportsBridgeImport { get; init; }
    }

    public static VulkanUpscaleBridgeProbeResult Probe(string? openGlVendor, string? openGlRenderer)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new VulkanUpscaleBridgeProbeResult
            {
                ProbeFailureReason = "Vulkan upscale bridge probing is only implemented for Windows in Phase 0.",
            };
        }

        try
        {
            Vk api = Vk.GetApi();
            Instance instance = default;
            byte* applicationName = null;
            byte* engineName = null;

            try
            {
                applicationName = (byte*)Marshal.StringToHGlobalAnsi("XRENGINE VulkanUpscaleBridgeProbe");
                engineName = (byte*)Marshal.StringToHGlobalAnsi("XRENGINE");

                ApplicationInfo appInfo = new()
                {
                    SType = StructureType.ApplicationInfo,
                    PApplicationName = applicationName,
                    ApplicationVersion = new Version32(1, 0, 0),
                    PEngineName = engineName,
                    EngineVersion = new Version32(1, 0, 0),
                    ApiVersion = Vk.Version11,
                };

                InstanceCreateInfo createInfo = new()
                {
                    SType = StructureType.InstanceCreateInfo,
                    PApplicationInfo = &appInfo,
                    EnabledExtensionCount = 0,
                    PpEnabledExtensionNames = null,
                    EnabledLayerCount = 0,
                    PpEnabledLayerNames = null,
                };

                Result instanceResult = api.CreateInstance(ref createInfo, null, out instance);
                if (instanceResult != Result.Success)
                {
                    return new VulkanUpscaleBridgeProbeResult
                    {
                        ProbeFailureReason = $"vkCreateInstance failed with {instanceResult}.",
                    };
                }

                if (!TrySelectDevice(api, instance, openGlVendor, openGlRenderer, out VulkanUpscaleBridgeSelectedDevice? selected, out string? selectionFailureReason) ||
                    selected is null)
                {
                    return new VulkanUpscaleBridgeProbeResult
                    {
                        ProbeFailureReason = selectionFailureReason ?? "No Vulkan physical device candidates were available for bridge probing.",
                    };
                }

                return new VulkanUpscaleBridgeProbeResult
                {
                    ProbeSucceeded = true,
                    HasVulkanExternalMemoryImport = selected.HasExternalMemory && selected.HasExternalMemoryWin32,
                    HasVulkanExternalSemaphoreImport = selected.HasExternalSemaphore && selected.HasExternalSemaphoreWin32,
                    SelectedDeviceName = selected.DeviceName,
                    SelectedVendorId = selected.VendorId,
                    SelectedDeviceId = selected.DeviceId,
                    SamePhysicalGpu = selected.SamePhysicalGpu,
                    GpuIdentityReason = selected.GpuIdentityReason,
                    ProbeFailureReason = selected.SupportsBridgeImport
                        ? null
                        : BuildMissingExtensionReason(selected),
                };
            }
            finally
            {
                if (instance.Handle != 0)
                    api.DestroyInstance(instance, null);

                if (applicationName is not null)
                    Marshal.FreeHGlobal((IntPtr)applicationName);
                if (engineName is not null)
                    Marshal.FreeHGlobal((IntPtr)engineName);
            }
        }
        catch (DllNotFoundException ex)
        {
            return new VulkanUpscaleBridgeProbeResult
            {
                ProbeFailureReason = $"Vulkan runtime is unavailable ({ex.Message}).",
            };
        }
        catch (Exception ex)
        {
            return new VulkanUpscaleBridgeProbeResult
            {
                ProbeFailureReason = $"Vulkan bridge probe failed: {ex.Message}",
            };
        }
    }

    private static HashSet<string> EnumerateDeviceExtensionNames(Vk api, PhysicalDevice device)
    {
        uint extensionCount = 0;
        api.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extensionCount, null);
        if (extensionCount == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var extensions = new ExtensionProperties[extensionCount];
        fixed (ExtensionProperties* extensionsPtr = extensions)
        {
            api.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extensionCount, extensionsPtr);
        }

        return extensions
            .Select(static ext => SilkMarshal.PtrToString((nint)ext.ExtensionName) ?? string.Empty)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Candidate? SelectBestCandidate(List<Candidate> candidates)
    {
        return candidates
            .OrderByDescending(static candidate => candidate.GraphicsQueueFamilyIndex.HasValue)
            .ThenByDescending(static candidate => candidate.RendererMatches)
            .ThenByDescending(static candidate => candidate.SupportsBridgeImport)
            .ThenByDescending(static candidate => candidate.VendorMatches)
            .ThenBy(static candidate => candidate.DeviceName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    internal static bool TrySelectDevice(
        Vk api,
        Instance instance,
        string? openGlVendor,
        string? openGlRenderer,
        out VulkanUpscaleBridgeSelectedDevice? selectedDevice,
        out string? failureReason)
    {
        selectedDevice = null;
        failureReason = null;

        uint deviceCount = 0;
        api.EnumeratePhysicalDevices(instance, ref deviceCount, null);
        if (deviceCount == 0)
        {
            failureReason = "No Vulkan physical devices were reported.";
            return false;
        }

        var devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* devicesPtr = devices)
        {
            api.EnumeratePhysicalDevices(instance, ref deviceCount, devicesPtr);
        }

        List<Candidate> candidates = new((int)deviceCount);
        foreach (PhysicalDevice device in devices)
        {
            api.GetPhysicalDeviceProperties(device, out PhysicalDeviceProperties properties);
            string deviceName = ReadDeviceName(properties);
            HashSet<string> extensionNames = EnumerateDeviceExtensionNames(api, device);
            bool hasGraphicsQueue = TryFindGraphicsQueueFamily(api, device, out uint graphicsQueueFamilyIndex);

            candidates.Add(new Candidate
            {
                Device = device,
                Properties = properties,
                ExtensionNames = extensionNames,
                DeviceName = deviceName,
                VendorId = properties.VendorID,
                DeviceId = properties.DeviceID,
                GraphicsQueueFamilyIndex = hasGraphicsQueue ? graphicsQueueFamilyIndex : null,
                HasExternalMemory = extensionNames.Contains("VK_KHR_external_memory"),
                HasExternalSemaphore = extensionNames.Contains("VK_KHR_external_semaphore"),
                HasExternalMemoryWin32 = extensionNames.Contains("VK_KHR_external_memory_win32"),
                HasExternalSemaphoreWin32 = extensionNames.Contains("VK_KHR_external_semaphore_win32"),
                RendererMatches = RendererMatches(openGlRenderer, deviceName),
                VendorMatches = VendorMatches(openGlVendor, properties.VendorID),
            });
        }

        Candidate? selected = SelectBestCandidate(candidates);
        if (selected is null)
        {
            failureReason = "No Vulkan physical device candidates were available for bridge probing.";
            return false;
        }

        if (!selected.GraphicsQueueFamilyIndex.HasValue)
        {
            failureReason = $"Selected Vulkan device '{selected.DeviceName}' has no graphics queue family suitable for the bridge sidecar.";
            return false;
        }

        (bool? samePhysicalGpu, string? gpuReason) = EvaluateGpuIdentity(candidates, selected);
        selectedDevice = new VulkanUpscaleBridgeSelectedDevice
        {
            Device = selected.Device,
            Properties = selected.Properties,
            ExtensionNames = selected.ExtensionNames,
            DeviceName = selected.DeviceName,
            VendorId = selected.VendorId,
            DeviceId = selected.DeviceId,
            GraphicsQueueFamilyIndex = selected.GraphicsQueueFamilyIndex.Value,
            HasExternalMemory = selected.HasExternalMemory,
            HasExternalSemaphore = selected.HasExternalSemaphore,
            HasExternalMemoryWin32 = selected.HasExternalMemoryWin32,
            HasExternalSemaphoreWin32 = selected.HasExternalSemaphoreWin32,
            SamePhysicalGpu = samePhysicalGpu,
            GpuIdentityReason = gpuReason,
            SupportsBridgeImport = selected.SupportsBridgeImport,
        };

        return true;
    }

    private static (bool? SamePhysicalGpu, string? Reason) EvaluateGpuIdentity(List<Candidate> candidates, Candidate selected)
    {
        List<Candidate> exactMatches = candidates.Where(static candidate => candidate.RendererMatches).ToList();
        if (exactMatches.Count == 1)
            return (true, $"OpenGL renderer matches Vulkan device '{selected.DeviceName}'.");

        if (exactMatches.Count > 1)
            return (false, "Multiple Vulkan devices match the OpenGL renderer string; refusing to risk cross-adapter imports.");

        List<Candidate> vendorMatches = candidates.Where(static candidate => candidate.VendorMatches).ToList();
        if (vendorMatches.Count == 0)
            return (false, $"OpenGL and Vulkan vendors do not match (selected Vulkan device '{selected.DeviceName}').");

        if (vendorMatches.Count > 1)
            return (false, "Multiple Vulkan devices share the OpenGL vendor, but none matched the OpenGL renderer string exactly.");

        return (false, $"OpenGL renderer did not match the selected Vulkan device ('{selected.DeviceName}').");
    }

    private static string BuildMissingExtensionReason(VulkanUpscaleBridgeSelectedDevice candidate)
    {
        List<string> missing = new(RequiredBridgeDeviceExtensions.Length);
        if (!candidate.HasExternalMemory)
            missing.Add("VK_KHR_external_memory");
        if (!candidate.HasExternalSemaphore)
            missing.Add("VK_KHR_external_semaphore");
        if (!candidate.HasExternalMemoryWin32)
            missing.Add("VK_KHR_external_memory_win32");
        if (!candidate.HasExternalSemaphoreWin32)
            missing.Add("VK_KHR_external_semaphore_win32");

        return missing.Count == 0
            ? string.Empty
            : $"Selected Vulkan device '{candidate.DeviceName}' is missing required bridge extensions: {string.Join(", ", missing)}.";
    }

    private static string ReadDeviceName(PhysicalDeviceProperties properties)
    {
        return SilkMarshal.PtrToString((nint)properties.DeviceName) ?? string.Empty;
    }

    private static bool TryFindGraphicsQueueFamily(Vk api, PhysicalDevice device, out uint queueFamilyIndex)
    {
        uint queueFamilyCount = 0;
        api.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, null);
        if (queueFamilyCount == 0)
        {
            queueFamilyIndex = 0;
            return false;
        }

        var properties = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* propertiesPtr = properties)
        {
            api.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, propertiesPtr);
        }

        for (uint index = 0; index < queueFamilyCount; index++)
        {
            if ((properties[index].QueueFlags & QueueFlags.GraphicsBit) == 0)
                continue;

            queueFamilyIndex = index;
            return true;
        }

        queueFamilyIndex = 0;
        return false;
    }

    private static bool RendererMatches(string? openGlRenderer, string? vulkanDeviceName)
    {
        if (string.IsNullOrWhiteSpace(openGlRenderer) || string.IsNullOrWhiteSpace(vulkanDeviceName))
            return false;

        string gl = NormalizeGpuName(openGlRenderer);
        string vk = NormalizeGpuName(vulkanDeviceName);
        if (string.IsNullOrEmpty(gl) || string.IsNullOrEmpty(vk))
            return false;

        return gl.Contains(vk, StringComparison.Ordinal) || vk.Contains(gl, StringComparison.Ordinal);
    }

    private static bool VendorMatches(string? openGlVendor, uint vulkanVendorId)
    {
        uint? openGlVendorId = TryMapOpenGlVendor(openGlVendor);
        return openGlVendorId.HasValue && openGlVendorId.Value == vulkanVendorId;
    }

    private static uint? TryMapOpenGlVendor(string? openGlVendor)
    {
        if (string.IsNullOrWhiteSpace(openGlVendor))
            return null;

        if (openGlVendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return NvidiaVendorId;
        if (openGlVendor.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return IntelVendorId;
        if (openGlVendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) || openGlVendor.Contains("ATI", StringComparison.OrdinalIgnoreCase))
            return AmdVendorId;

        return null;
    }

    private static string NormalizeGpuName(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int index = 0;
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
                buffer[index++] = char.ToLowerInvariant(c);
        }

        return index == 0 ? string.Empty : new string(buffer[..index]);
    }
}