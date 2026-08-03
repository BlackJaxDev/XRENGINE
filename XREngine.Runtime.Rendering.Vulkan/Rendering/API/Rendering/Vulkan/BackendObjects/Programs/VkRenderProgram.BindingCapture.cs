using System.Threading;
using XREngine.Data.Rendering;

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
        internal Dictionary<string, ProgramUniformValue> Uniforms = new(StringComparer.Ordinal);
        internal Dictionary<string, VulkanRuntimeUniformPublication>
            RuntimeUniformPublications = new(StringComparer.Ordinal);
        internal HashSet<string> MutableLegacyUniformNames =
            new(StringComparer.Ordinal);
        internal Dictionary<uint, XRTexture> SamplersByUnit = [];
        internal Dictionary<uint, string> SamplerNamesByUnit = [];
        internal Dictionary<string, XRTexture> SamplersByName = new(StringComparer.Ordinal);
        internal Dictionary<uint, ProgramImageBinding> ImagesByUnit = [];
        internal Dictionary<uint, XRDataBuffer> BuffersByBinding { get; } = [];
        internal int TypedPublicationDepth;
        internal int TypedResourcePublicationDepth;
        internal EVulkanBindingFrequency TypedPublicationFrequency;
        internal ulong TypedPublicationGeneration;
        internal int MutableLegacyPublicationDepth;

        internal void Clear()
        {
            Uniforms.Clear();
            RuntimeUniformPublications.Clear();
            MutableLegacyUniformNames.Clear();
            SamplersByUnit.Clear();
            SamplerNamesByUnit.Clear();
            SamplersByName.Clear();
            ImagesByUnit.Clear();
            BuffersByBinding.Clear();
        }

        internal void RecordUniform(string name)
        {
            if (TypedPublicationDepth == 0)
            {
                RuntimeUniformPublications.Remove(name);
                if (MutableLegacyPublicationDepth != 0)
                    MutableLegacyUniformNames.Add(name);
                else
                    MutableLegacyUniformNames.Remove(name);
                return;
            }

            MutableLegacyUniformNames.Remove(name);
            RuntimeUniformPublications[name] =
                new VulkanRuntimeUniformPublication(
                    TypedPublicationFrequency,
                    TypedPublicationGeneration);
        }

        internal void RejectTypedResourceWrite(string resourceKind)
        {
            if (TypedPublicationDepth == 0 ||
                TypedResourcePublicationDepth != 0)
                return;

            throw new InvalidOperationException(
                $"Typed binding publishers may only publish numeric uniforms; " +
                $"{resourceKind} resources require the legacy descriptor path.");
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

    internal TypedBindingPublicationScope BeginTypedBindingPublication(
        ERenderBindingFrequency frequency,
        ulong generation)
        => new(this, frequency, generation);

    /// <summary>
    /// Publishes descriptor resources under an exact owner frequency and
    /// generation while retaining typed ownership for any tightly coupled
    /// numeric metadata emitted by the resource publisher.
    /// </summary>
    internal TypedBindingPublicationScope BeginTypedResourceBindingPublication(
        ERenderBindingFrequency frequency,
        ulong generation)
        => new(this, frequency, generation, allowResourceWrites: true);

    /// <summary>
    /// Marks numeric writes from an untyped callback so immutable snapshots can
    /// distinguish callback-owned UBO values from ordinary material and engine
    /// bindings. Descriptor-only callbacks leave this set empty.
    /// </summary>
    internal MutableLegacyBindingPublicationScope
        BeginMutableLegacyBindingPublication()
        => new(this);

    internal readonly ref struct MutableLegacyBindingPublicationScope
    {
        private readonly BindingCaptureState? _capture;

        internal MutableLegacyBindingPublicationScope(VkRenderProgram owner)
        {
            if (owner.TryGetActiveBindingCaptureState(
                    out BindingCaptureState capture))
            {
                _capture = capture;
                _capture.MutableLegacyPublicationDepth++;
            }
            else
                _capture = null;
        }

        public void Dispose()
        {
            if (_capture is not null)
                _capture.MutableLegacyPublicationDepth--;
        }
    }

    internal readonly ref struct TypedBindingPublicationScope
    {
        private readonly BindingCaptureState _capture;
        private readonly EVulkanBindingFrequency _previousFrequency;
        private readonly ulong _previousGeneration;
        private readonly bool _allowsResourceWrites;

        internal TypedBindingPublicationScope(
            VkRenderProgram owner,
            ERenderBindingFrequency frequency,
            ulong generation,
            bool allowResourceWrites = false)
        {
            if (frequency is <= ERenderBindingFrequency.Unknown or
                >= ERenderBindingFrequency.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frequency),
                    frequency,
                    "Typed binding publishers must declare a concrete frequency.");
            }
            if (generation == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(generation),
                    "Typed binding publishers must declare a non-zero content generation.");
            }
            if (!owner.TryGetActiveBindingCaptureState(out _capture))
            {
                throw new InvalidOperationException(
                    "Typed binding publishers may only run inside a private Vulkan binding capture.");
            }

            _previousFrequency = _capture.TypedPublicationFrequency;
            _previousGeneration = _capture.TypedPublicationGeneration;
            _capture.TypedPublicationFrequency =
                ToVulkanBindingFrequency(frequency);
            _capture.TypedPublicationGeneration = generation;
            _capture.TypedPublicationDepth++;
            _allowsResourceWrites = allowResourceWrites;
            if (_allowsResourceWrites)
                _capture.TypedResourcePublicationDepth++;
        }

        public void Dispose()
        {
            if (_allowsResourceWrites)
                _capture.TypedResourcePublicationDepth--;
            _capture.TypedPublicationDepth--;
            _capture.TypedPublicationFrequency = _previousFrequency;
            _capture.TypedPublicationGeneration = _previousGeneration;
        }

        private static EVulkanBindingFrequency ToVulkanBindingFrequency(
            ERenderBindingFrequency frequency)
            => frequency switch
            {
                ERenderBindingFrequency.Frame => EVulkanBindingFrequency.Frame,
                ERenderBindingFrequency.View => EVulkanBindingFrequency.View,
                ERenderBindingFrequency.Pass => EVulkanBindingFrequency.Pass,
                ERenderBindingFrequency.Material => EVulkanBindingFrequency.Material,
                ERenderBindingFrequency.Object => EVulkanBindingFrequency.Object,
                ERenderBindingFrequency.Instance => EVulkanBindingFrequency.Instance,
                ERenderBindingFrequency.RuntimeCallback => EVulkanBindingFrequency.RuntimeCallback,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(frequency),
                    frequency,
                    "Typed binding publishers must declare a supported frequency."),
            };
    }
}
