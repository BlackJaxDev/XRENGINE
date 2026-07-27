namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkRenderProgram
    {
        [ThreadStatic]
        private static BindingCaptureState? t_activeBindingCaptureState;
        [ThreadStatic]
        private static BindingCaptureState? t_freeBindingCaptureStates;

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
            BindingCaptureState state = t_freeBindingCaptureStates ?? new BindingCaptureState();
            if (t_freeBindingCaptureStates is not null)
                t_freeBindingCaptureStates = state.NextFree;

            state.NextFree = null;
            state.Parent = t_activeBindingCaptureState;
            state.Owner = this;
            t_activeBindingCaptureState = state;
            return state;
        }

        private void PopBindingCapture(BindingCaptureState state)
        {
            if (!ReferenceEquals(t_activeBindingCaptureState, state) ||
                !ReferenceEquals(state.Owner, this))
            {
                throw new InvalidOperationException("Vulkan program binding captures must be disposed in stack order.");
            }

            t_activeBindingCaptureState = state.Parent;
            state.Owner = null;
            state.Parent = null;
            state.NextFree = t_freeBindingCaptureStates;
            t_freeBindingCaptureStates = state;
        }

        private bool TryGetActiveBindingCaptureState(out BindingCaptureState state)
        {
            state = t_activeBindingCaptureState!;
            return state is not null && ReferenceEquals(state.Owner, this);
        }

        /// <summary>
        /// Routes multicast XRRenderProgram callbacks to the backend instance that
        /// opened the current capture. With no capture, callers retain the locked
        /// immediate-binding behavior.
        /// </summary>
        private bool TryResolveBindingWriteState(out BindingCaptureState? state)
        {
            state = t_activeBindingCaptureState;
            if (state is null)
                return true;

            if (ReferenceEquals(state.Owner, this))
                return true;

            state = null;
            return false;
        }
    }
}
