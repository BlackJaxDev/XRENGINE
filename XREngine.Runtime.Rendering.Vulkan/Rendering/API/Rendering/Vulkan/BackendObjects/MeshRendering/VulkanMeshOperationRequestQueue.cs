namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded, allocation-free handoff for immutable mesh operation requests.
/// The queue deliberately retains no renderer authority; its consumer supplies
/// planning, output, command, resource, and telemetry state at drain time.
/// </summary>
internal sealed class VulkanMeshOperationRequestQueue
{
    private const int Capacity = 1024;
    private readonly VulkanMeshRenderRequest[] _entries = new VulkanMeshRenderRequest[Capacity];
    private readonly object _gate = new();
    private int _head;
    private int _count;

    internal bool TryEnqueue(in VulkanMeshRenderRequest request)
    {
        lock (_gate)
        {
            if (_count == Capacity)
                return false;

            int tail = _head + _count;
            if (tail >= Capacity)
                tail -= Capacity;
            _entries[tail] = request;
            _count++;
            return true;
        }
    }

    internal bool TryDequeue(out VulkanMeshRenderRequest request)
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                request = default;
                return false;
            }

            request = _entries[_head];
            _entries[_head] = default;
            _head++;
            if (_head == Capacity)
                _head = 0;
            _count--;
            return true;
        }
    }
}
