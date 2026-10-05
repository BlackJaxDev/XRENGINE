namespace XREngine.Rendering;

/// <summary>
/// Frame-slot aggregate-deformation output generation.
/// </summary>
internal sealed class AdvancedGpuDeformationOutputBuffers
{
    public AdvancedGpuDeformationOutputBuffers(
        int frameSlotCount,
        uint vertexCapacity)
    {
        Buffers =
            new XRDataBuffer<AdvancedDeformedVertex>[frameSlotCount];
        for (int slot = 0; slot < frameSlotCount; slot++)
        {
            // Only the deformation compute pass writes these slots, so they keep
            // no CPU copy: GPU storage is allocated from the element metadata.
            Buffers[slot] = new XRDataBuffer<AdvancedDeformedVertex>(
                $"AdvancedDeformation.Output.Slot{slot}",
                // ArrayBuffer requests vertex usage while the Vulkan backend retains storage usage for compute writes.
                EBufferTarget.ArrayBuffer,
                vertexCapacity,
                allocateClientSideSource: false)
            {
                Usage = EBufferUsage.StaticCopy,
                DisposeOnPush = false,
                Resizable = false,
                GpuProduced = true,
            };
        }
        VertexCapacity = vertexCapacity;
    }

    public XRDataBuffer<AdvancedDeformedVertex>[] Buffers { get; }
    public uint VertexCapacity { get; }

    public void Destroy()
    {
        for (int slot = 0; slot < Buffers.Length; slot++)
            Buffers[slot].Destroy();
    }
}
