using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

internal sealed class OpenXrEyeRecordWorker : IDisposable
{
    private readonly int _workerIndex;
    private readonly AutoResetEvent _workAvailable = new(false);
    private readonly ManualResetEventSlim _workCompleted = new(true);
    private readonly Thread _thread;
    private VulkanOpenXrCommandRecordingService? _recordingService;
    private OpenXrPreparedEyeRecordWorkerInput _prepared;
    private OpenXrEyeRecordWorkerResult _result;
    private bool _stopping;

    public OpenXrEyeRecordWorker(int workerIndex)
    {
        _workerIndex = workerIndex;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"OpenXR Vulkan eye record worker {workerIndex}"
        };
        _thread.Start();
    }

    public void Start(VulkanOpenXrCommandRecordingService recordingService, in OpenXrPreparedEyeRecordWorkerInput prepared)
    {
        _workCompleted.Reset();
        _recordingService = recordingService;
        _prepared = prepared;
        _result = default;
        _workAvailable.Set();
    }

    public OpenXrEyeRecordWorkerResult Wait()
    {
        _workCompleted.Wait();
        return _result;
    }

    private void Run()
    {
        while (true)
        {
            _workAvailable.WaitOne();
            if (_stopping)
                return;

            VulkanOpenXrCommandRecordingService? recordingService = _recordingService;
            if (recordingService is null)
            {
                _result = new OpenXrEyeRecordWorkerResult(false, default, Environment.CurrentManagedThreadId, TimeSpan.Zero, "worker has no command-recording service");
                _workCompleted.Set();
                continue;
            }

            long start = Stopwatch.GetTimestamp();
            int threadId = Environment.CurrentManagedThreadId;
            try
            {
                bool success = recordingService.TryRecordPreparedEye(
                    _workerIndex,
                    _prepared,
                    out OpenXrRecordedEyeCommandBuffer recorded,
                    out VulkanImportedTexturePendingUpload[] recordedUploads);
                long end = Stopwatch.GetTimestamp();
                _result = new OpenXrEyeRecordWorkerResult(
                    success,
                    recorded,
                    threadId,
                    Stopwatch.GetElapsedTime(start, end),
                    null,
                    start,
                    end,
                    recordedUploads);
            }
            catch (Exception ex)
            {
                long end = Stopwatch.GetTimestamp();
                _result = new OpenXrEyeRecordWorkerResult(
                    false,
                    default,
                    threadId,
                    Stopwatch.GetElapsedTime(start, end),
                    ex.Message,
                    start,
                    end);
            }
            finally
            {
                _recordingService = null;
                _workCompleted.Set();
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _workAvailable.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(2)))
        {
            Debug.VulkanWarning(
                "[OpenXR] Timed out waiting for Vulkan eye record worker {0} to stop.",
                _workerIndex);
        }

        _workCompleted.Dispose();
        _workAvailable.Dispose();
    }
}
