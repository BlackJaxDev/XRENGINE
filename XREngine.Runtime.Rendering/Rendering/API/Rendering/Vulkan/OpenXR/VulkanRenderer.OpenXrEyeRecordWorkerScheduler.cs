using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed class OpenXrEyeRecordWorkerScheduler : IDisposable
    {
        private readonly OpenXrEyeRecordWorker _left = new(0);
        private readonly OpenXrEyeRecordWorker _right = new(1);

        public OpenXrEyeRecordWorkerBatchResult Record(
            VulkanRenderer renderer,
            in OpenXrPreparedEyeCommandBufferInput leftEye,
            in OpenXrPreparedEyeCommandBufferInput rightEye)
        {
            _left.Start(renderer, leftEye);
            _right.Start(renderer, rightEye);

            long waitStart = Stopwatch.GetTimestamp();
            OpenXrEyeRecordWorkerResult left = _left.Wait();
            OpenXrEyeRecordWorkerResult right = _right.Wait();
            TimeSpan waitTime = Stopwatch.GetElapsedTime(waitStart);

            if (OpenXrVulkanTraceEnabled)
            {
                Debug.Vulkan(
                    "[OpenXrVulkan] eye record workers completed leftSuccess={0} rightSuccess={1} leftThread={2} rightThread={3} leftMs={4:F3} rightMs={5:F3} waitMs={6:F3}",
                    left.Success,
                    right.Success,
                    left.ThreadId,
                    right.ThreadId,
                    left.RecordTime.TotalMilliseconds,
                    right.RecordTime.TotalMilliseconds,
                    waitTime.TotalMilliseconds);
            }

            return new OpenXrEyeRecordWorkerBatchResult(left, right, waitTime);
        }

        public void Dispose()
        {
            _left.Dispose();
            _right.Dispose();
        }
    }
}
