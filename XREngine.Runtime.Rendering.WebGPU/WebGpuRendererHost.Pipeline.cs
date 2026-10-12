using System.Buffers.Binary;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    public void ConfigurePipeline(BrowserPipelineQualitySettings settings)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(settings);
        WebGpuImports.ConfigurePipeline(_session, settings.ToJson());
    }

    public void ConfigureMaterial(BrowserResourceHandle material, BrowserMaterialData data)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(data);
        if (!_resources.Contains(material.Packed))
            throw new InvalidOperationException("Material must belong to this WebGPU renderer.");
        WebGpuImports.ConfigurePipelineMaterial(_session, material.Packed, data.ToPipelineJson());
    }

    public void SubmitPipelinePacket(BrowserPipelineFramePacket packet)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(packet);
        Span<byte> bytes = packet.BeginConsume();
        try
        {
            if (!TryDescribeFrameOutput(out RenderFrameOutputDescription output) ||
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(16)) != _session ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(20)) != output.TargetGeneration ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(32)) != output.Properties.Width ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(36)) != output.Properties.Height)
                throw new InvalidOperationException("Pipeline frame owner, surface generation or extent is obsolete.");
            WebGpuImports.SubmitPipelinePacket(_session, bytes);
            SetField(ref _submittedFrame, true);
        }
        finally { packet.EndConsume(); }
    }
}
