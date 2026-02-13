using System;
using System.IO;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private PipelineCache _pipelineCache;
    private string? _pipelineCacheFilePath;

    internal PipelineCache ActivePipelineCache
        => _pipelineCache;

    private void CreateVulkanPipelineCache()
    {
        if (device.Handle == 0)
            return;

        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XREngine",
            "Vulkan",
            "PipelineCache");

        _pipelineCacheFilePath = Path.Combine(
            cacheDir,
            $"pcache_v{properties.VendorID:X8}_{properties.DeviceID:X8}_{properties.DriverVersion:X8}_{properties.ApiVersion:X8}.bin");

        byte[]? initialData = null;
        if (!string.IsNullOrWhiteSpace(_pipelineCacheFilePath) && File.Exists(_pipelineCacheFilePath))
        {
            try
            {
                initialData = File.ReadAllBytes(_pipelineCacheFilePath);
                if (initialData.Length == 0)
                    initialData = null;
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning($"[Vulkan] Failed to read pipeline cache file '{_pipelineCacheFilePath}': {ex.Message}");
            }
        }

        fixed (byte* initialDataPtr = initialData)
        {
            PipelineCacheCreateInfo info = new()
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = initialData is null ? 0u : (nuint)initialData.Length,
                PInitialData = initialDataPtr,
            };

            Result result = Api.CreatePipelineCache(device, ref info, null, out _pipelineCache);
            if (result != Result.Success)
            {
                _pipelineCache = default;
                Debug.VulkanWarning($"[Vulkan] Failed to create pipeline cache ({result}); continuing without persistent cache.");
                return;
            }
        }

        Debug.Vulkan(
            "[Vulkan] Pipeline cache initialised (path={0}, warmBytes={1}).",
            _pipelineCacheFilePath ?? "<unset>",
            initialData?.Length ?? 0);
    }

    private void SaveVulkanPipelineCache()
    {
        if (_pipelineCache.Handle == 0 || string.IsNullOrWhiteSpace(_pipelineCacheFilePath))
            return;

        try
        {
            nuint cacheSize = 0;
            Result sizeResult = Api!.GetPipelineCacheData(device, _pipelineCache, &cacheSize, null);
            if (sizeResult != Result.Success || cacheSize == 0)
                return;

            byte[] cacheBytes = new byte[(int)cacheSize];
            fixed (byte* cachePtr = cacheBytes)
            {
                Result dataResult = Api.GetPipelineCacheData(device, _pipelineCache, &cacheSize, cachePtr);
                if (dataResult != Result.Success)
                {
                    Debug.VulkanWarning($"[Vulkan] Failed to fetch pipeline cache data ({dataResult}).");
                    return;
                }
            }

            string path = _pipelineCacheFilePath!;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllBytes(path, cacheBytes);

            Debug.Vulkan("[Vulkan] Pipeline cache saved ({0} bytes).", cacheBytes.Length);
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[Vulkan] Failed to save pipeline cache '{_pipelineCacheFilePath}': {ex.Message}");
        }
    }

    private void DestroyVulkanPipelineCache()
    {
        if (_pipelineCache.Handle == 0)
            return;

        SaveVulkanPipelineCache();
        Api!.DestroyPipelineCache(device, _pipelineCache, null);
        _pipelineCache = default;
    }
}