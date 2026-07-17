namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record ComputeDispatchSnapshot(
        Dictionary<string, ProgramUniformValue> Uniforms,
        Dictionary<uint, XRTexture> Samplers,
        Dictionary<uint, string> SamplerNamesByUnit,
        Dictionary<string, XRTexture> SamplersByName,
        Dictionary<uint, ProgramImageBinding> Images,
        Dictionary<uint, XRDataBuffer> Buffers,
        Dictionary<string, XRDataBuffer> BuffersByName)
    {
        public ComputeDispatchSnapshot(
            Dictionary<string, ProgramUniformValue> uniforms,
            Dictionary<uint, XRTexture> samplers,
            Dictionary<uint, string> samplerNamesByUnit,
            Dictionary<string, XRTexture> samplersByName,
            Dictionary<uint, ProgramImageBinding> images,
            Dictionary<uint, XRDataBuffer> buffers)
            : this(
                uniforms,
                samplers,
                samplerNamesByUnit,
                samplersByName,
                images,
                buffers,
                BuildBuffersByName(buffers))
        {
        }

        private static Dictionary<string, XRDataBuffer> BuildBuffersByName(Dictionary<uint, XRDataBuffer> buffers)
        {
            Dictionary<string, XRDataBuffer> buffersByName = new(StringComparer.Ordinal);
            foreach (XRDataBuffer buffer in buffers.Values)
            {
                if (!string.IsNullOrWhiteSpace(buffer.AttributeName))
                    buffersByName.TryAdd(buffer.AttributeName, buffer);
            }

            return buffersByName;
        }
    }
}
