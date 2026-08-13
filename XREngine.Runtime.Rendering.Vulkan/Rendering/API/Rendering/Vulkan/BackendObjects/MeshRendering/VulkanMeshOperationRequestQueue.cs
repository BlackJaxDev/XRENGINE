namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded, allocation-free handoff for immutable mesh operation requests.
/// The queue deliberately retains no renderer authority; its consumer supplies
/// planning, output, command, resource, and telemetry state at drain time.
/// </summary>
internal sealed class VulkanMeshOperationRequestQueue
{
    // A frame can contain a full shadow-caster cohort followed by the main-view
    // and composition draws.  Keeping only one 1K cohort allowed the shadow
    // pass to consume the entire queue, dropping the later fullscreen present
    // draw while the camera was moving.  Four cohorts cover the current
    // directional-cascade workload without allocating in the render hot path.
    internal const int Capacity = 4096;
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

    /// <summary>
    /// Moves the currently published request cohort into caller-owned storage
    /// under one short queue lock. New producers may begin publishing the next
    /// cohort as soon as the copy completes; Vulkan resource preparation never
    /// runs while the handoff gate is held.
    /// </summary>
    internal int DrainTo(Span<VulkanMeshRenderRequest> destination)
    {
        lock (_gate)
        {
            int drainCount = Math.Min(_count, destination.Length);
            int firstCount = Math.Min(drainCount, Capacity - _head);
            _entries.AsSpan(_head, firstCount).CopyTo(destination);
            _entries.AsSpan(_head, firstCount).Clear();

            int secondCount = drainCount - firstCount;
            if (secondCount > 0)
            {
                _entries.AsSpan(0, secondCount).CopyTo(destination[firstCount..]);
                _entries.AsSpan(0, secondCount).Clear();
            }

            _head += drainCount;
            if (_head >= Capacity)
                _head -= Capacity;
            _count -= drainCount;
            return drainCount;
        }
    }
}
