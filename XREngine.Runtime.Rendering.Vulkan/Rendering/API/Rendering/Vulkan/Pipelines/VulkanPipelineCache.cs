using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int VulkanPipelineCacheSchemaVersion = 3;
    private const int VulkanPipelineCompileRequiredResult = 1000297000;
    private const uint VulkanPipelineFailOnCompileRequiredFlag = 0x00000100;
    // Persist during normal editor startup rather than relying on orderly process
    // teardown. A typical unit-testing world creates more than this many GPL
    // pieces/links, while the interval guard keeps repeated disk writes bounded.
    private const int PipelineCacheAutoSaveCreateThreshold = 64;
    private const long PipelineCacheAutoSaveMinIntervalMs = 30_000;

    internal PipelineCache ActivePipelineCache
        => ResourceRuntime.PipelineManager._pipelineCache;

    /// <summary>
    /// A cache synchronization domain dedicated to native background compiles.
    /// Sharing the foreground cache lets a long driver compile serialize later
    /// render-thread pipeline creation inside the Vulkan implementation.
    /// </summary>
    internal PipelineCache BackgroundPipelineCache
        => ResourceRuntime.PipelineManager._backgroundPipelineCache;

    internal bool HasPersistedVulkanPipelineCacheData
        => ResourceRuntime.PipelineManager._pipelineCache.Handle != 0 && ResourceRuntime.PipelineManager._pipelineCacheInitialDataBytes > 0;

    private void CreateVulkanPipelineCache()
    {
        if (_deviceContext.Device.Handle == 0)
            return;

        Api!.GetPhysicalDeviceProperties(_deviceContext.PhysicalDevice, out PhysicalDeviceProperties properties);
        InitializeVulkanPipelinePrewarmDatabase(properties);

        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XREngine",
            "Vulkan",
            "PipelineCache");

        ResourceRuntime.PipelineManager._pipelineCacheFilePath = Path.Combine(
            cacheDir,
            $"pcache_xr{VulkanPipelineCacheSchemaVersion}_v{properties.VendorID:X8}_{properties.DeviceID:X8}_{properties.DriverVersion:X8}_{properties.ApiVersion:X8}.bin");

        byte[]? initialData = null;
        if (!string.IsNullOrWhiteSpace(ResourceRuntime.PipelineManager._pipelineCacheFilePath) && File.Exists(ResourceRuntime.PipelineManager._pipelineCacheFilePath))
        {
            try
            {
                initialData = File.ReadAllBytes(ResourceRuntime.PipelineManager._pipelineCacheFilePath);
                if (initialData.Length == 0)
                    initialData = null;
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning($"[Vulkan] Failed to read pipeline cache file '{ResourceRuntime.PipelineManager._pipelineCacheFilePath}': {ex.Message}");
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

            Result result = Api.CreatePipelineCache(_deviceContext.Device, ref info, null, out ResourceRuntime.PipelineManager._pipelineCache);
            if (result != Result.Success)
            {
                ResourceRuntime.PipelineManager._pipelineCache = default;
                Debug.VulkanWarning($"[Vulkan] Failed to create pipeline cache ({result}); continuing without persistent cache.");
                return;
            }

            Result backgroundResult = Api.CreatePipelineCache(
                _deviceContext.Device,
                ref info,
                null,
                out ResourceRuntime.PipelineManager._backgroundPipelineCache);
            if (backgroundResult != Result.Success)
            {
                ResourceRuntime.PipelineManager._backgroundPipelineCache = default;
                Debug.VulkanWarning(
                    "[Vulkan] Failed to create isolated background pipeline cache ({0}); " +
                    "background compiles will use no cache rather than serialize foreground creation.",
                    backgroundResult);
            }
        }

        ResourceRuntime.PipelineManager._pipelineCacheInitialDataBytes = initialData?.Length ?? 0;

        Debug.Vulkan(
            "[Vulkan] Pipeline cache initialised (path={0}, warmBytes={1}, vendor=0x{2:X8}, device=0x{3:X8}, driver=0x{4:X8}, api=0x{5:X8}).",
            ResourceRuntime.PipelineManager._pipelineCacheFilePath ?? "<unset>",
            initialData?.Length ?? 0,
            properties.VendorID,
            properties.DeviceID,
            properties.DriverVersion,
            properties.ApiVersion);
    }

    /// <summary>
    /// Publishes pipeline-cache entries produced by the isolated compiler cache
    /// into the foreground persistent cache after a native compile returns.
    /// </summary>
    internal void PublishVulkanBackgroundPipelineCache(double compileMilliseconds)
    {
        Result mergeResult;
        lock (ResourceRuntime.PipelineManager._pipelineCacheHostAccessLock)
            lock (ResourceRuntime.PipelineManager._backgroundPipelineCacheHostAccessLock)
            {
                if (ResourceRuntime.PipelineManager._pipelineCache.Handle == 0 || ResourceRuntime.PipelineManager._backgroundPipelineCache.Handle == 0)
                    return;

                PipelineCache source = ResourceRuntime.PipelineManager._backgroundPipelineCache;
                mergeResult = Api!.MergePipelineCaches(
                    _deviceContext.Device,
                    ResourceRuntime.PipelineManager._pipelineCache,
                    1,
                    &source);
            }

        if (mergeResult != Result.Success)
        {
            Debug.VulkanWarning(
                "[Vulkan] Failed to merge isolated background pipeline cache ({0}).",
                mergeResult);
            return;
        }

        // Expensive cold compiles are exactly the entries that must survive a
        // forced or impatient editor exit. Fast cache hits use the existing
        // threshold/interval policy to avoid repeated large disk writes.
        if (compileMilliseconds >= 1_000.0)
            QueueVulkanPipelineCacheAutoSave();
    }

    internal Result CreateGraphicsPipelineWithCachePolicy(
        ref GraphicsPipelineCreateInfo pipelineInfo,
        PipelineCache pipelineCache,
        bool backgroundCompile,
        out Pipeline pipeline)
    {
        long start = global::System.Diagnostics.Stopwatch.GetTimestamp();
        PipelineCreateFlags originalFlags = pipelineInfo.Flags;
        bool probedCache =
            backgroundCompile &&
            ResourceRuntime.PipelineManager._supportsPipelineCreationCacheControl &&
            pipelineCache.Handle != 0;
        bool compileRequired = false;
        Result result;

        if (probedCache)
        {
            pipelineInfo.Flags |= (PipelineCreateFlags)VulkanPipelineFailOnCompileRequiredFlag;
            result = CreateGraphicsPipelinesSynchronized(pipelineCache, ref pipelineInfo, out pipeline);
            pipelineInfo.Flags = originalFlags;
            if ((int)result == VulkanPipelineCompileRequiredResult)
            {
                compileRequired = true;
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineTelemetry(
                    EVulkanPipelineTelemetryEvent.CompileRequired,
                    EVulkanDriverPipelineCacheOutcome.Miss,
                    backgroundCompile: true);
                result = CreateGraphicsPipelinesSynchronized(pipelineCache, ref pipelineInfo, out pipeline);
            }
        }
        else
        {
            result = CreateGraphicsPipelinesSynchronized(pipelineCache, ref pipelineInfo, out pipeline);
        }

        double elapsedMs = global::System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        EVulkanDriverPipelineCacheOutcome cacheOutcome = compileRequired
            ? EVulkanDriverPipelineCacheOutcome.Miss
            : probedCache
                ? ResourceRuntime.PipelineManager._pipelineCacheInitialDataBytes > 0
                    ? EVulkanDriverPipelineCacheOutcome.PersistedHit
                    : EVulkanDriverPipelineCacheOutcome.RuntimeHit
                : EVulkanDriverPipelineCacheOutcome.Unknown;
        if (result == Result.Success)
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineTelemetry(
                EVulkanPipelineTelemetryEvent.CreationCompleted,
                cacheOutcome,
                backgroundCompile,
                elapsedMs);
            if (backgroundCompile)
            {
                Debug.Vulkan(
                    "[Vulkan] Background graphics pipeline cache probe completed (outcome={0}, elapsedMs={1:F2}, persistedBytes={2}).",
                    cacheOutcome,
                    elapsedMs,
                    ResourceRuntime.PipelineManager._pipelineCacheInitialDataBytes);
            }
        }
        return result;
    }

    /// <summary>
    /// Serializes host access to the cache used by a graphics-pipeline creation call.
    /// Vulkan requires external synchronization for every operation that accesses the
    /// same <see cref="PipelineCache"/> object.
    /// </summary>
    internal Result CreateGraphicsPipelinesSynchronized(
        PipelineCache pipelineCache,
        ref GraphicsPipelineCreateInfo pipelineInfo,
        out Pipeline pipeline)
    {
        if (pipelineCache.Handle == 0)
            return Api!.CreateGraphicsPipelines(_deviceContext.Device, pipelineCache, 1, ref pipelineInfo, null, out pipeline);

        lock (GetVulkanPipelineCacheHostAccessLock(pipelineCache))
            return Api!.CreateGraphicsPipelines(_deviceContext.Device, pipelineCache, 1, ref pipelineInfo, null, out pipeline);
    }

    /// <summary>
    /// Serializes host access to the cache used by a compute-pipeline creation call.
    /// </summary>
    internal Result CreateComputePipelinesSynchronized(
        PipelineCache pipelineCache,
        ref ComputePipelineCreateInfo pipelineInfo,
        out Pipeline pipeline)
    {
        if (pipelineCache.Handle == 0)
            return Api!.CreateComputePipelines(_deviceContext.Device, pipelineCache, 1, ref pipelineInfo, null, out pipeline);

        lock (GetVulkanPipelineCacheHostAccessLock(pipelineCache))
            return Api!.CreateComputePipelines(_deviceContext.Device, pipelineCache, 1, ref pipelineInfo, null, out pipeline);
    }

    private Lock GetVulkanPipelineCacheHostAccessLock(PipelineCache pipelineCache)
        => pipelineCache.Handle == ResourceRuntime.PipelineManager._backgroundPipelineCache.Handle
            ? ResourceRuntime.PipelineManager._backgroundPipelineCacheHostAccessLock
            : ResourceRuntime.PipelineManager._pipelineCacheHostAccessLock;

    private bool TryCaptureVulkanPipelineCacheData(out string path, out byte[] cacheBytes)
    {
        path = string.Empty;
        cacheBytes = [];
        if (ResourceRuntime.PipelineManager._pipelineCache.Handle == 0 || string.IsNullOrWhiteSpace(ResourceRuntime.PipelineManager._pipelineCacheFilePath))
            return false;

        try
        {
            lock (ResourceRuntime.PipelineManager._pipelineCacheHostAccessLock)
            {
                if (ResourceRuntime.PipelineManager._pipelineCache.Handle == 0)
                    return false;
                nuint cacheSize = 0;
                Result sizeResult = Api!.GetPipelineCacheData(_deviceContext.Device, ResourceRuntime.PipelineManager._pipelineCache, &cacheSize, null);
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
                    Result dataResult = Api.GetPipelineCacheData(_deviceContext.Device, ResourceRuntime.PipelineManager._pipelineCache, &cacheSize, cachePtr);
                    if (dataResult != Result.Success)
                    {
                        Debug.VulkanWarning($"[Vulkan] Failed to fetch pipeline cache data ({dataResult}).");
                        return false;
                    }
                }
            }

            path = ResourceRuntime.PipelineManager._pipelineCacheFilePath!;
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[Vulkan] Failed to capture pipeline cache data '{ResourceRuntime.PipelineManager._pipelineCacheFilePath}': {ex.Message}");
            return false;
        }
    }

    private bool WriteVulkanPipelineCacheFile(string path, byte[] cacheBytes, long generation, bool skipIfStale)
    {
        try
        {
            global::System.Diagnostics.Stopwatch saveWatch = global::System.Diagnostics.Stopwatch.StartNew();
            lock (ResourceRuntime.PipelineManager._pipelineCacheFileWriteLock)
            {
                if (skipIfStale && Volatile.Read(ref ResourceRuntime.PipelineManager._pipelineCacheSaveGeneration) != generation)
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

        long generation = Interlocked.Increment(ref ResourceRuntime.PipelineManager._pipelineCacheSaveGeneration);
        if (WriteVulkanPipelineCacheFile(path, cacheBytes, generation, skipIfStale: false))
            Interlocked.Exchange(ref ResourceRuntime.PipelineManager._pipelineCacheCreatesSinceSave, 0);
    }

    private void QueueVulkanPipelineCacheAutoSave()
    {
        if (Interlocked.CompareExchange(ref ResourceRuntime.PipelineManager._pipelineCacheAutoSaveInFlight, 1, 0) != 0)
            return;

        if (!TryCaptureVulkanPipelineCacheData(out string path, out byte[] cacheBytes))
        {
            Interlocked.Exchange(ref ResourceRuntime.PipelineManager._pipelineCacheAutoSaveInFlight, 0);
            return;
        }

        Interlocked.Exchange(ref ResourceRuntime.PipelineManager._pipelineCacheCreatesSinceSave, 0);
        long generation = Interlocked.Increment(ref ResourceRuntime.PipelineManager._pipelineCacheSaveGeneration);
        _ = Task.Run(() =>
        {
            try
            {
                WriteVulkanPipelineCacheFile(path, cacheBytes, generation, skipIfStale: true);
            }
            finally
            {
                Interlocked.Exchange(ref ResourceRuntime.PipelineManager._pipelineCacheAutoSaveInFlight, 0);
            }
        });
    }

    internal void NotifyVulkanPipelineCreated(string kind)
    {
        if (ResourceRuntime.PipelineManager._pipelineCache.Handle == 0)
            return;

        int createsSinceSave = Interlocked.Increment(ref ResourceRuntime.PipelineManager._pipelineCacheCreatesSinceSave);
        if (createsSinceSave < PipelineCacheAutoSaveCreateThreshold)
            return;

        long now = Environment.TickCount64;
        if (ResourceRuntime.PipelineManager._pipelineCacheLastAutoSaveAttemptTick != 0 &&
            unchecked(now - ResourceRuntime.PipelineManager._pipelineCacheLastAutoSaveAttemptTick) < PipelineCacheAutoSaveMinIntervalMs)
            return;

        ResourceRuntime.PipelineManager._pipelineCacheLastAutoSaveAttemptTick = now;
        Debug.Vulkan("[Vulkan] Pipeline cache auto-save threshold reached after {0} new {1} pipeline(s).", createsSinceSave, kind);
        QueueVulkanPipelineCacheAutoSave();
    }

    private void DestroyVulkanPipelineCache()
    {
        SaveVulkanPipelinePrewarmDatabase();

        lock (ResourceRuntime.PipelineManager._pipelineCacheHostAccessLock)
            lock (ResourceRuntime.PipelineManager._backgroundPipelineCacheHostAccessLock)
                if (ResourceRuntime.PipelineManager._pipelineCache.Handle != 0 && ResourceRuntime.PipelineManager._backgroundPipelineCache.Handle != 0)
                {
                    PipelineCache source = ResourceRuntime.PipelineManager._backgroundPipelineCache;
                    Result mergeResult = Api!.MergePipelineCaches(
                        _deviceContext.Device,
                        ResourceRuntime.PipelineManager._pipelineCache,
                        1,
                        &source);
                    if (mergeResult != Result.Success)
                    {
                        Debug.VulkanWarning(
                            "[Vulkan] Final background pipeline cache merge failed ({0}).",
                            mergeResult);
                    }
                }

        lock (ResourceRuntime.PipelineManager._backgroundPipelineCacheHostAccessLock)
            if (ResourceRuntime.PipelineManager._backgroundPipelineCache.Handle != 0)
            {
                Api!.DestroyPipelineCache(_deviceContext.Device, ResourceRuntime.PipelineManager._backgroundPipelineCache, null);
                ResourceRuntime.PipelineManager._backgroundPipelineCache = default;
            }

        if (ResourceRuntime.PipelineManager._pipelineCache.Handle != 0)
        {
            SaveVulkanPipelineCache();
            lock (ResourceRuntime.PipelineManager._pipelineCacheHostAccessLock)
            {
                Api!.DestroyPipelineCache(_deviceContext.Device, ResourceRuntime.PipelineManager._pipelineCache, null);
                ResourceRuntime.PipelineManager._pipelineCache = default;
            }
        }

        ResourceRuntime.PipelineManager._pipelineCacheInitialDataBytes = 0;
    }
}
