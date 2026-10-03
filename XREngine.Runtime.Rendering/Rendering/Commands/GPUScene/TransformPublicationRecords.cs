using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Dense renderer-owned rows copied directly from the hierarchy's publication slots.
/// Canonical scene transactions copy these values into their independently versioned temporal rows.
/// </summary>
public sealed class TransformPublicationRecords : ITransformPublicationSink
{
    private readonly object _gate = new();
    private readonly TransformHierarchyStore _store;
    private TransformGpu[] _gpu = [];
    private AdvancedTransformRecord[] _advanced = [];
    private uint[] _generations = [];

    public TransformPublicationRecords(TransformHierarchyStore store) => _store = store;

    public void Publish(ReadOnlySpan<int> slots, ReadOnlySpan<Matrix4x4> matrices, ReadOnlySpan<uint> generations)
    {
        lock (_gate)
        {
            if (_gpu.Length < matrices.Length)
            {
                Array.Resize(ref _gpu, matrices.Length);
                Array.Resize(ref _advanced, matrices.Length);
                Array.Resize(ref _generations, matrices.Length);
            }
            foreach (int slot in slots)
            {
                _gpu[slot] = new TransformGpu(matrices[slot]);
                _advanced[slot] = new AdvancedTransformRecord { World = matrices[slot] };
                _generations[slot] = generations[slot];
            }
        }
    }

    /// <summary>Returns a published record only for the current identity and exact captured model matrix.</summary>
    public bool TryGet(TransformHandle handle, in Matrix4x4 capturedWorld, out TransformGpu gpu, out AdvancedTransformRecord advanced)
    {
        lock (_gate)
        {
            if ((uint)handle.Index < (uint)_gpu.Length && handle.Generation != 0
                && _generations[handle.Index] == handle.Generation && _store.IsAlive(handle)
                && _gpu[handle.Index].WorldMatrix.Equals(capturedWorld))
            {
                gpu = _gpu[handle.Index];
                advanced = _advanced[handle.Index];
                return true;
            }
        }
        gpu = default;
        advanced = default;
        return false;
    }

    internal static bool TryGet(RenderCommand? command, in Matrix4x4 world, out TransformGpu gpu, out AdvancedTransformRecord advanced)
    {
        if (command?.OwnerRenderInfo?.Owner is XREngine.Components.Scene.Mesh.RenderableComponent component
            && component.Transform.WorldAs<IRuntimeRenderWorld>() is RuntimeWorldRenderer renderer
            && renderer.TransformRecords is { } records)
            return records.TryGet(component.Transform.HierarchyHandle, world, out gpu, out advanced);
        gpu = default;
        advanced = default;
        return false;
    }
}
