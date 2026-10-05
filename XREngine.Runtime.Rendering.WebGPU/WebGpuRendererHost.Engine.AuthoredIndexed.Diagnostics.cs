using System.Text;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    /// <summary>Captures the bounded retained authored indexed owners between engine frames.</summary>
    public string GetAuthoredIndexedCacheDiagnostics()
    {
        RequireEngineFrameStatisticsBoundary();
        StringBuilder output = new();
        output.Append("{\"owner\":").Append(_session)
            .Append(",\"frameSequence\":").Append(_engineFrameSequence)
            .Append(",\"outputGeneration\":").Append(_submittedEngineSurfaceGeneration)
            .Append(",\"slotCapacity\":3")
            .Append(",\"sourceCapacity\":").Append(WebGpuAuthoredIndexedFrameSlot.MaximumSources)
            .Append(",\"drawCapacity\":").Append(WebGpuAuthoredIndexedFrameSlot.MaximumDraws)
            .Append(",\"slots\":[");
        if (_authoredIndexedSlots is { } slots)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (i != 0) output.Append(',');
                slots[i].AppendCacheDiagnostics(output, i);
            }
        }
        return output.Append("]}").ToString();
    }
}
