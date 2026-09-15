using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Serializes OpenXR runtime calls that may access Vulkan's graphics queue.
/// Admission precedes queue serialization so exclusive device transitions can
/// close both paths without a queue operation escaping their lifetime gate.
/// </summary>
internal readonly struct VulkanOpenXrRuntimeQueueLease : IDisposable
{
    private readonly ReaderWriterLockSlim? _admissionGate;
    private readonly VulkanQueueOperationLease _queueOperation;

    private VulkanOpenXrRuntimeQueueLease(
        ReaderWriterLockSlim admissionGate,
        VulkanQueueOperationLease queueOperation)
    {
        _admissionGate = admissionGate;
        _queueOperation = queueOperation;
    }

    public bool Acquired => _admissionGate is not null && _queueOperation.Acquired;

    internal static VulkanOpenXrRuntimeQueueLease Enter(
        ReaderWriterLockSlim admissionGate,
        object queueGate,
        VulkanDeviceStateMachine deviceState,
        VulkanFrameTelemetry telemetry,
        string operation)
    {
        admissionGate.EnterReadLock();
        try
        {
            VulkanQueueOperationLease queueOperation = VulkanQueueOperationLease.TryEnter(
                queueGate,
                deviceState,
                telemetry);
            if (!queueOperation.Acquired)
            {
                throw new InvalidOperationException(
                    $"Cannot execute OpenXR Vulkan runtime operation '{operation}' while device state is {deviceState.State}.");
            }

            return new VulkanOpenXrRuntimeQueueLease(admissionGate, queueOperation);
        }
        catch
        {
            admissionGate.ExitReadLock();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            _queueOperation.Dispose();
        }
        finally
        {
            _admissionGate?.ExitReadLock();
        }
    }
}
