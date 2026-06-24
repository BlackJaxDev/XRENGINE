using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int PipelineCacheAutoSaveCreateThreshold = 256;
    private const long PipelineCacheAutoSaveMinIntervalMs = 30_000;
    private PipelineCache _pipelineCache;
    private string? _pipelineCacheFilePath;
    private int _pipelineCacheCreatesSinceSave;
    private long _pipelineCacheLastAutoSaveAttemptTick;
    private int _pipelineCacheAutoSaveInFlight;
    private long _pipelineCacheSaveGeneration;
    private readonly object _pipelineCacheFileWriteLock = new();

    internal PipelineCache ActivePipelineCache
        => _pipelineCache;

    private void CreateVulkanPipelineCache()
    {
        if (device.Handle == 0)
            return;

        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        InitializeVulkanPipelinePrewarmDatabase(properties);

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
            "[Vulkan] Pipeline cache initialised (path={0}, warmBytes={1}, vendor=0x{2:X8}, device=0x{3:X8}, driver=0x{4:X8}, api=0x{5:X8}).",
            _pipelineCacheFilePath ?? "<unset>",
            initialData?.Length ?? 0,
            properties.VendorID,
            properties.DeviceID,
            properties.DriverVersion,
            properties.ApiVersion);
    }

    private bool TryCaptureVulkanPipelineCacheData(out string path, out byte[] cacheBytes)
    {
        path = string.Empty;
        cacheBytes = [];
        if (_pipelineCache.Handle == 0 || string.IsNullOrWhiteSpace(_pipelineCacheFilePath))
            return false;

        try
        {
            nuint cacheSize = 0;
            Result sizeResult = Api!.GetPipelineCacheData(device, _pipelineCache, &cacheSize, null);
            if (sizeResult != Result.Success || cacheSize == 0)
            {
                Debug.VulkanWarning($"[Vulkan] Pipeline cache save skipped: sizeResult={sizeResult}, size={cacheSize}.");
                return false;
            }

            if (cacheSize > int.MaxValue)
            {
                Debug.VulkanWarning($"[Vulkan] Pipeline cache save skipped: cache is too large ({cacheSize} bytes).");
                return false;
            }

            cacheBytes = new byte[(int)cacheSize];
            fixed (byte* cachePtr = cacheBytes)
            {
                Result dataResult = Api.GetPipelineCacheData(device, _pipelineCache, &cacheSize, cachePtr);
                if (dataResult != Result.Success)
                {
                    Debug.VulkanWarning($"[Vulkan] Failed to fetch pipeline cache data ({dataResult}).");
                    return false;
                }
            }

            path = _pipelineCacheFilePath!;
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[Vulkan] Failed to capture pipeline cache data '{_pipelineCacheFilePath}': {ex.Message}");
            return false;
        }
    }

    private bool WriteVulkanPipelineCacheFile(string path, byte[] cacheBytes, long generation, bool skipIfStale)
    {
        try
        {
            global::System.Diagnostics.Stopwatch saveWatch = global::System.Diagnostics.Stopwatch.StartNew();
            lock (_pipelineCacheFileWriteLock)
            {
                if (skipIfStale && Volatile.Read(ref _pipelineCacheSaveGeneration) != generation)
                {
                    Debug.Vulkan("[Vulkan] Pipeline cache async save skipped because a newer save was requested.");
                    return false;
                }

                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllBytes(path, cacheBytes);
            }

            saveWatch.Stop();
            Debug.Vulkan("[Vulkan] Pipeline cache saved (path={0}, bytes={1}, elapsedMs={2:F2}).", path, cacheBytes.Length, saveWatch.Elapsed.TotalMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[Vulkan] Failed to save pipeline cache '{path}': {ex.Message}");
            return false;
        }
    }

    private void SaveVulkanPipelineCache()
    {
        if (!TryCaptureVulkanPipelineCacheData(out string path, out byte[] cacheBytes))
            return;

        long generation = Interlocked.Increment(ref _pipelineCacheSaveGeneration);
        if (WriteVulkanPipelineCacheFile(path, cacheBytes, generation, skipIfStale: false))
            _pipelineCacheCreatesSinceSave = 0;
    }

    private void QueueVulkanPipelineCacheAutoSave()
    {
        if (Interlocked.CompareExchange(ref _pipelineCacheAutoSaveInFlight, 1, 0) != 0)
            return;

        if (!TryCaptureVulkanPipelineCacheData(out string path, out byte[] cacheBytes))
        {
            Interlocked.Exchange(ref _pipelineCacheAutoSaveInFlight, 0);
            return;
        }

        _pipelineCacheCreatesSinceSave = 0;
        long generation = Interlocked.Increment(ref _pipelineCacheSaveGeneration);
        _ = Task.Run(() =>
        {
            try
            {
                WriteVulkanPipelineCacheFile(path, cacheBytes, generation, skipIfStale: true);
            }
            finally
            {
                Interlocked.Exchange(ref _pipelineCacheAutoSaveInFlight, 0);
            }
        });
    }

    internal void NotifyVulkanPipelineCreated(string kind)
    {
        if (_pipelineCache.Handle == 0)
            return;

        _pipelineCacheCreatesSinceSave++;
        if (_pipelineCacheCreatesSinceSave < PipelineCacheAutoSaveCreateThreshold)
            return;

        long now = Environment.TickCount64;
        if (_pipelineCacheLastAutoSaveAttemptTick != 0 &&
            unchecked(now - _pipelineCacheLastAutoSaveAttemptTick) < PipelineCacheAutoSaveMinIntervalMs)
            return;

        _pipelineCacheLastAutoSaveAttemptTick = now;
        Debug.Vulkan("[Vulkan] Pipeline cache auto-save threshold reached after {0} new {1} pipeline(s).", _pipelineCacheCreatesSinceSave, kind);
        QueueVulkanPipelineCacheAutoSave();
    }

    private void DestroyVulkanPipelineCache()
    {
        SaveVulkanPipelinePrewarmDatabase();

        if (_pipelineCache.Handle == 0)
            return;

        SaveVulkanPipelineCache();
        Api!.DestroyPipelineCache(device, _pipelineCache, null);
        _pipelineCache = default;
    }
}
