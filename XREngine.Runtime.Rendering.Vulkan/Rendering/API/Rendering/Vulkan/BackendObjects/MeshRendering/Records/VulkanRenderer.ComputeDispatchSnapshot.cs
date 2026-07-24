namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record ComputeDispatchSnapshot(
        Dictionary<string, ProgramUniformValue> Uniforms,
        Dictionary<uint, XRTexture> Samplers,
        Dictionary<uint, string> SamplerNamesByUnit,
        Dictionary<string, XRTexture> SamplersByName,
        Dictionary<uint, ProgramImageBinding> Images,
        Dictionary<uint, VulkanComputeBufferBinding> Buffers,
        Dictionary<string, VulkanComputeBufferBinding> BuffersByName)
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
                BuildBindings(buffers),
                BuildBuffersByName(buffers))
        {
        }

        private static Dictionary<uint, VulkanComputeBufferBinding> BuildBindings(Dictionary<uint, XRDataBuffer> buffers)
        {
            Dictionary<uint, VulkanComputeBufferBinding> bindings = new(buffers.Count);
            foreach (KeyValuePair<uint, XRDataBuffer> pair in buffers)
                bindings[pair.Key] = new VulkanComputeBufferBinding(pair.Value, default, 0UL, 0);
            return bindings;
        }

        private static Dictionary<string, VulkanComputeBufferBinding> BuildBuffersByName(Dictionary<uint, XRDataBuffer> buffers)
        {
            Dictionary<string, VulkanComputeBufferBinding> buffersByName = new(StringComparer.Ordinal);
            foreach (XRDataBuffer buffer in buffers.Values)
            {
                if (!string.IsNullOrWhiteSpace(buffer.AttributeName))
                    buffersByName.TryAdd(buffer.AttributeName, new VulkanComputeBufferBinding(buffer, default, 0UL, 0));
            }

            return buffersByName;
        }
    }
}
