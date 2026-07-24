using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed class OpenXrEyeRecordWorker : IDisposable
    {
        private readonly int _workerIndex;
        private readonly AutoResetEvent _workAvailable = new(false);
        private readonly ManualResetEventSlim _workCompleted = new(true);
        private readonly Thread _thread;
        private VulkanRenderer? _renderer;
        private OpenXrPreparedEyeCommandBufferInput _prepared;
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

        public void Start(VulkanRenderer renderer, in OpenXrPreparedEyeCommandBufferInput prepared)
        {
            _workCompleted.Reset();
            _renderer = renderer;
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

                VulkanRenderer? renderer = _renderer;
                if (renderer is null)
                {
                    _result = new OpenXrEyeRecordWorkerResult(false, default, Environment.CurrentManagedThreadId, TimeSpan.Zero, "worker has no renderer");
                    _workCompleted.Set();
                    continue;
                }

                long start = Stopwatch.GetTimestamp();
                int threadId = Environment.CurrentManagedThreadId;
                try
                {
                    bool success = renderer.TryRecordOpenXrEyeSwapchainCommandBufferFromWorker(
                        _workerIndex,
                        _prepared,
                        out OpenXrRecordedEyeCommandBuffer recorded);
                    _result = new OpenXrEyeRecordWorkerResult(
                        success,
                        recorded,
                        threadId,
                        Stopwatch.GetElapsedTime(start),
                        null);
                }
                catch (Exception ex)
                {
                    _result = new OpenXrEyeRecordWorkerResult(
                        false,
                        default,
                        threadId,
                        Stopwatch.GetElapsedTime(start),
                        ex.Message);
                }
                finally
                {
                    _renderer = null;
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
}
