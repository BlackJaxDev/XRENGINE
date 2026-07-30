using System.Threading;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkRenderProgram
{
    private static readonly ThreadLocal<BindingCaptureWorkspace> BindingCaptureWorkspaces =
        new(static () => new BindingCaptureWorkspace(), trackAllValues: false);

    private static BindingCaptureWorkspace CurrentBindingCaptureWorkspace
        => BindingCaptureWorkspaces.Value
            ?? throw new InvalidOperationException(
                "The Vulkan program binding-capture workspace has been disposed.");

    internal static void ReleaseCurrentThreadBindingCaptureWorkspace()
        => CurrentBindingCaptureWorkspace.Reset();

    private sealed class BindingCaptureWorkspace
    {
        public BindingCaptureState? Active;
        public BindingCaptureState? Free;

        public void Reset()
        {
            Active = null;
            Free = null;
        }
    }

    /// <summary>
    /// Per-thread mutable writer used while callbacks assemble one immutable draw
    /// snapshot. It prevents independent views from serializing through a program's
    /// legacy immediate-binding dictionaries.
    /// </summary>
    private sealed class BindingCaptureState
    {
        private readonly List<ComputeDispatchSnapshot> _frameSnapshots = [];
        private ulong _frameSnapshotFrame;
        private int _frameSnapshotCursor;

        internal VkRenderProgram? Owner;
        internal BindingCaptureState? Parent;
        internal BindingCaptureState? NextFree;
        internal Dictionary<string, ProgramUniformValue> Uniforms { get; } = new(StringComparer.Ordinal);
        internal Dictionary<uint, XRTexture> SamplersByUnit { get; } = [];
        internal Dictionary<uint, string> SamplerNamesByUnit { get; } = [];
        internal Dictionary<string, XRTexture> SamplersByName { get; } = new(StringComparer.Ordinal);
        internal Dictionary<uint, ProgramImageBinding> ImagesByUnit { get; } = [];
        internal Dictionary<uint, XRDataBuffer> BuffersByBinding { get; } = [];

        internal void Clear()
        {
            Uniforms.Clear();
            SamplersByUnit.Clear();
            SamplerNamesByUnit.Clear();
            SamplersByName.Clear();
            ImagesByUnit.Clear();
            BuffersByBinding.Clear();
        }

        internal void SetSampler(string name, XRTexture texture, uint unit)
        {
            SamplersByUnit[unit] = texture;
            if (!string.IsNullOrWhiteSpace(name))
            {
                SamplerNamesByUnit[unit] = name;
                SamplersByName[name] = texture;
            }
            else
            {
                SamplerNamesByUnit.Remove(unit);
            }
        }

        internal ComputeDispatchSnapshot? RentFrameSnapshot()
        {
            if (RuntimeRenderingHostServices.FrameTiming.CurrentRenderPipelineContext is null)
                return null;

            ulong frameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
            if (frameId == 0)
                return null;

            if (_frameSnapshotFrame != frameId)
            {
                _frameSnapshotFrame = frameId;
                _frameSnapshotCursor = 0;
            }

            int index = _frameSnapshotCursor++;
            if (index < _frameSnapshots.Count)
                return _frameSnapshots[index];

            ComputeDispatchSnapshot snapshot = new();
            _frameSnapshots.Add(snapshot);
            return snapshot;
        }
    }

    private BindingCaptureState PushBindingCapture()
    {
        BindingCaptureState state = CurrentBindingCaptureWorkspace.Free ?? new BindingCaptureState();
        if (CurrentBindingCaptureWorkspace.Free is not null)
            CurrentBindingCaptureWorkspace.Free = state.NextFree;

        state.NextFree = null;
        state.Parent = CurrentBindingCaptureWorkspace.Active;
        state.Owner = this;
        CurrentBindingCaptureWorkspace.Active = state;
        return state;
    }

    private void PopBindingCapture(BindingCaptureState state)
    {
        if (!ReferenceEquals(CurrentBindingCaptureWorkspace.Active, state) ||
            !ReferenceEquals(state.Owner, this))
        {
            throw new InvalidOperationException("Vulkan program binding captures must be disposed in stack order.");
        }

        CurrentBindingCaptureWorkspace.Active = state.Parent;
        state.Owner = null;
        state.Parent = null;
        state.NextFree = CurrentBindingCaptureWorkspace.Free;
        CurrentBindingCaptureWorkspace.Free = state;
    }

    private bool TryGetActiveBindingCaptureState(out BindingCaptureState state)
    {
        state = CurrentBindingCaptureWorkspace.Active!;
        return state is not null && ReferenceEquals(state.Owner, this);
    }

    /// <summary>
    /// Routes multicast XRRenderProgram callbacks to the backend instance that
    /// opened the current capture. With no capture, callers retain the locked
    /// immediate-binding behavior.
    /// </summary>
    private bool TryResolveBindingWriteState(out BindingCaptureState? state)
    {
        state = CurrentBindingCaptureWorkspace.Active;
        if (state is null)
            return true;

        if (ReferenceEquals(state.Owner, this))
            return true;

        state = null;
        return false;
    }
}
