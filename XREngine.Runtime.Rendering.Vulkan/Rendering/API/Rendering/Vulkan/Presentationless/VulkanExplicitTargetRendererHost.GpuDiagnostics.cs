using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public sealed unsafe partial class VulkanExplicitTargetRendererHost
{
    /// <summary>Optional pass sink installed only while recording an explicit production frame.</summary>
    public IVulkanSelectedGpuPassSink? SelectedGpuPassSink { get; set; }

    /// <summary>Completes all host-owned queue submissions before diagnostic resources are released.</summary>
    public void CompleteDiagnosticGpuWork()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Result result = Api.DeviceWaitIdle(Device);
        if (result != Result.Success)
            throw new InvalidOperationException($"Vulkan diagnostic GPU completion failed: {result}.");
    }

    private void ValidateSelectedGpuPassQueue()
    {
        if (SelectedGpuPassSink is null)
            return;
        if (RuntimeEngine.EffectiveSettings.VulkanQueueOverlapMode != EVulkanQueueOverlapMode.GraphicsOnly)
            throw new NotSupportedException(
                "Selected GPU pass timestamps require GraphicsOnly queue mode; split graphics/compute/transfer submissions need per-queue timestamp boundaries.");
    }

    /// <summary>Uses synchronization2 timestamps when the selected device enabled that feature.</summary>
    public void WriteDiagnosticTimestamp(CommandBuffer commandBuffer, bool begin, QueryPool pool, uint query)
    {
        var device = _renderer.DeviceContext;
        if (device.SupportsSynchronization2)
        {
            PipelineStageFlags2 stage = begin
                ? PipelineStageFlags2.TopOfPipeBit
                : PipelineStageFlags2.BottomOfPipeBit;
            if (device.InstanceApiVersion >= Vk.Version13)
            {
                Api.CmdWriteTimestamp2(commandBuffer, stage, pool, query);
                return;
            }
            if (device.ExtensionFunctions.KhrSynchronization2 is { } synchronization2)
            {
                synchronization2.CmdWriteTimestamp2(commandBuffer, stage, pool, query);
                return;
            }
        }
        Api.CmdWriteTimestamp(commandBuffer,
            begin ? PipelineStageFlags.TopOfPipeBit : PipelineStageFlags.BottomOfPipeBit,
            pool, query);
    }
}
