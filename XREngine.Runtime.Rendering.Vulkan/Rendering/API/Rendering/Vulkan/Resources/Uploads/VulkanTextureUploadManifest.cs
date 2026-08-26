namespace XREngine.Rendering.Vulkan;

/// <summary>Frozen set of uploads required by one PresentNow frame.</summary>
internal sealed class VulkanTextureUploadManifest
{
    private const int Capacity = VulkanAcceptedFramePlan.UploadCapacity;
    private readonly long[] _sequences = new long[Capacity];
    private int _count;

    public bool IsEmpty => _count == 0;
    internal int Count => _count;

    internal void BeginCapture()
    {
        _sequences.AsSpan(0, _count).Clear();
        _count = 0;
    }

    internal void Add(in VulkanTextureUploadTicket ticket)
    {
        if (!ticket.IsValid)
            return;
        for (int index = 0; index < _count; index++)
            if (_sequences[index] == ticket.Sequence)
                return;
        if (_count == _sequences.Length)
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Upload,
                _sequences.Length,
                _count + 1);
        _sequences[_count++] = ticket.Sequence;
    }

    public bool Contains(in VulkanTextureUploadTicket ticket)
    {
        if (!ticket.IsValid)
            return false;
        for (int index = 0; index < _count; index++)
            if (_sequences[index] == ticket.Sequence)
                return true;
        return false;
    }
}
