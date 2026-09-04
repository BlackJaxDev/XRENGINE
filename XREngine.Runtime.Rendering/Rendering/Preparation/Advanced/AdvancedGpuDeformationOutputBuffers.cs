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
            Buffers[slot] = new XRDataBuffer<AdvancedDeformedVertex>(
                $"AdvancedDeformation.Output.Slot{slot}",
                // ArrayBuffer requests vertex usage while the Vulkan backend retains storage usage for compute writes.
                EBufferTarget.ArrayBuffer,
                vertexCapacity)
            {
                Usage = EBufferUsage.StaticCopy,
                DisposeOnPush = false,
                Resizable = false,
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
