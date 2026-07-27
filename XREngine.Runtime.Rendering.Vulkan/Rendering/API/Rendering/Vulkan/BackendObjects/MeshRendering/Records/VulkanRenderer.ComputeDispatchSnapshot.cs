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

        internal bool HasPublishedBindingLayoutSignatures { get; private set; }
        internal ulong UniformBindingLayoutSignature { get; private set; }
        internal ulong SamplerUnitBindingLayoutSignature { get; private set; }
        internal ulong SamplerNameBindingLayoutSignature { get; private set; }
        internal ulong ImageBindingLayoutSignature { get; private set; }
        internal ulong BufferBindingLayoutSignature { get; private set; }
        internal ulong DescriptorSetLayoutSignature { get; private set; }

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
            HasPublishedBindingLayoutSignatures = false;
            UniformBindingLayoutSignature = CopyUniformsAndComputeLayoutSignature(uniforms, Uniforms);
            Copy(samplers, Samplers);
            Copy(samplerNamesByUnit, SamplerNamesByUnit);
            Copy(samplersByName, SamplersByName);
            Copy(images, Images);
            Buffers.Clear();
            BuffersByName.Clear();
            SamplerUnitBindingLayoutSignature = 0;
            SamplerNameBindingLayoutSignature = 0;
            ImageBindingLayoutSignature = 0;
            BufferBindingLayoutSignature = 0;
            DescriptorSetLayoutSignature = 0;
        }

        /// <summary>
        /// Resolves a sampler from this captured binding set without consulting the
        /// program's mutable binding dictionaries.
        /// </summary>
        internal bool TryGetSamplerTexture(string samplerName, out XRTexture? texture)
        {
            texture = null;
            return !string.IsNullOrEmpty(samplerName) &&
                SamplersByName.TryGetValue(samplerName, out texture);
        }

        /// <summary>
        /// Publishes immutable descriptor-layout fingerprints after all captured
        /// buffer handles have been resolved. Uniform layout hashing is folded
        /// into the copy above so command-buffer signature validation does not
        /// rescan every uniform name and type later in the same frame.
        /// </summary>
        internal void PublishBindingLayoutSignatures()
        {
            SamplerUnitBindingLayoutSignature = HashSamplerUnitBindingLayout(Samplers, SamplerNamesByUnit);
            SamplerNameBindingLayoutSignature = HashSamplerNameBindingLayout(SamplersByName);
            ImageBindingLayoutSignature = HashImageBindingLayout(Images);
            BufferBindingLayoutSignature = HashBufferBindingLayout(Buffers);

            FrameOpSignatureHasher hash = new();
            hash.Add(1);
            hash.Add(UniformBindingLayoutSignature);
            hash.Add(SamplerUnitBindingLayoutSignature);
            hash.Add(SamplerNameBindingLayoutSignature);
            hash.Add(ImageBindingLayoutSignature);
            hash.Add(BufferBindingLayoutSignature);
            DescriptorSetLayoutSignature = hash.ToHash();
            HasPublishedBindingLayoutSignatures = true;
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

        private static ulong CopyUniformsAndComputeLayoutSignature(
            Dictionary<string, ProgramUniformValue> source,
            Dictionary<string, ProgramUniformValue> destination)
        {
            destination.Clear();
            destination.EnsureCapacity(source.Count);

            ulong xor = 0;
            ulong sum = 0;
            foreach (KeyValuePair<string, ProgramUniformValue> pair in source)
            {
                destination[pair.Key] = pair.Value;

                FrameOpSignatureHasher item = new();
                item.Add(pair.Key);
                item.Add((int)pair.Value.Type);
                item.Add(pair.Value.IsArray);
                AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
            }

            return FinishUnorderedHash(source.Count, xor, sum);
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
