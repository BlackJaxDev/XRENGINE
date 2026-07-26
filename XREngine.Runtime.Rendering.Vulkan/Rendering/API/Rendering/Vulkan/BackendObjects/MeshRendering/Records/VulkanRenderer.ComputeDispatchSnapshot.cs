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
        public ComputeDispatchSnapshot()
            : this(
                new Dictionary<string, ProgramUniformValue>(StringComparer.Ordinal),
                new Dictionary<uint, XRTexture>(),
                new Dictionary<uint, string>(),
                new Dictionary<string, XRTexture>(StringComparer.Ordinal),
                new Dictionary<uint, ProgramImageBinding>(),
                new Dictionary<uint, VulkanComputeBufferBinding>(),
                new Dictionary<string, VulkanComputeBufferBinding>(StringComparer.Ordinal))
        {
        }

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

        /// <summary>
        /// Replaces captured bindings while retaining dictionary storage. Desktop
        /// frame snapshots are short-lived CPU recording data, so a per-program
        /// frame pool can reuse their backing arrays after the previous frame has
        /// completed command recording.
        /// </summary>
        public void Reset(
            Dictionary<string, ProgramUniformValue> uniforms,
            Dictionary<uint, XRTexture> samplers,
            Dictionary<uint, string> samplerNamesByUnit,
            Dictionary<string, XRTexture> samplersByName,
            Dictionary<uint, ProgramImageBinding> images)
        {
            Copy(uniforms, Uniforms);
            Copy(samplers, Samplers);
            Copy(samplerNamesByUnit, SamplerNamesByUnit);
            Copy(samplersByName, SamplersByName);
            Copy(images, Images);
            Buffers.Clear();
            BuffersByName.Clear();
        }

        private static void Copy<TKey, TValue>(
            Dictionary<TKey, TValue> source,
            Dictionary<TKey, TValue> destination)
            where TKey : notnull
        {
            destination.Clear();
            destination.EnsureCapacity(source.Count);
            foreach (KeyValuePair<TKey, TValue> pair in source)
                destination[pair.Key] = pair.Value;
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
