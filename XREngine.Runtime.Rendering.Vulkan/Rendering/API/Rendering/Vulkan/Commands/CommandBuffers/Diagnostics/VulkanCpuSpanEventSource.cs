using System.Diagnostics.Tracing;

namespace XREngine.Rendering.Vulkan;

/// <summary>Optional stable EventPipe and ETW marker for selected CPU scopes.</summary>
[EventSource(Name = "XREngine-Vulkan-CpuSpans")]
public sealed class VulkanCpuSpanEventSource : EventSource
{
    public static readonly VulkanCpuSpanEventSource Log = new();

    private VulkanCpuSpanEventSource() { }

    [Event(1, Level = EventLevel.Informational)]
    public unsafe void Span(long spanId, long frameId, int stageId, int threadId, int workerId, long startTimestamp, long endTimestamp)
    {
        if (!IsEnabled())
            return;
        EventData* data = stackalloc EventData[7];
        data[0].DataPointer = (IntPtr)(&spanId); data[0].Size = sizeof(long);
        data[1].DataPointer = (IntPtr)(&frameId); data[1].Size = sizeof(long);
        data[2].DataPointer = (IntPtr)(&stageId); data[2].Size = sizeof(int);
        data[3].DataPointer = (IntPtr)(&threadId); data[3].Size = sizeof(int);
        data[4].DataPointer = (IntPtr)(&workerId); data[4].Size = sizeof(int);
        data[5].DataPointer = (IntPtr)(&startTimestamp); data[5].Size = sizeof(long);
        data[6].DataPointer = (IntPtr)(&endTimestamp); data[6].Size = sizeof(long);
        WriteEventCore(1, 7, data);
    }
}
