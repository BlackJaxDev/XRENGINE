using System;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Compute;

namespace XREngine.Rendering.Gaussian;

/// <summary>
/// Draws gaussian splats back to front as instanced screen-space quads. Static and animated splat
/// components share this renderer.
/// </summary>
/// <remarks>
/// <para>
/// A producer fills <see cref="Splats"/> and <see cref="VisibleCount"/>, from the CPU with
/// <see cref="Upload"/> or from compute shaders that increment <see cref="VisibleCount"/> atomically.
/// Each frame, <see cref="Prepare()"/> runs in the PreRender pass: it writes one depth key for each
/// splat slot and sorts the keys on the GPU. Then <see cref="Draw"/> runs in the TransparentForward pass:
/// it draws <see cref="Capacity"/> quad instances in sorted order. Instances at or past the visible count
/// make no fragments.
/// </para>
/// <para>
/// The quad material blends premultiplied color with source factor One and destination factor
/// OneMinusSrcAlpha. It tests depth with Lequal and does not write depth.
/// </para>
/// <para>
/// Call the methods on the render thread. <see cref="Prepare()"/> and <see cref="Draw"/> do not allocate
/// after the first frame. The draw binds the buffers in the quad's uniform callback, so the backend must
/// run that callback inside <c>XRMeshRenderer.Render</c>, as the OpenGL backend does.
/// </para>
/// </remarks>
public sealed partial class GaussianSplatRenderer : IDisposable
{
    /// <summary>The largest capacity that the renderer accepts.</summary>
    public const int MaxCapacity = 1 << 24;

    /// <summary>Storage binding of <see cref="Splats"/> in the splat shaders.</summary>
    public const uint SplatsBinding = 46u;
    /// <summary>Storage binding of the sorted (key, splat index) pairs in the splat shaders.</summary>
    public const uint OrderBinding = 47u;
    /// <summary>Storage binding of <see cref="VisibleCount"/> in the splat shaders.</summary>
    public const uint VisibleCountBinding = 48u;
    /// <summary>Storage binding of <see cref="Overflow"/> for splat producers.</summary>
    public const uint OverflowBinding = 49u;

    /// <summary>Invocations in one workgroup of the depth key shader.</summary>
    public const uint KeysLocalSize = 256u;

    /// <summary>
    /// The opacity that marks an empty slot. A real splat has an opacity from 0 to 1, so the depth key pass gives a slot
    /// with a negative (or NaN) opacity the culled key, and the slot does not draw.
    /// </summary>
    public const float EmptySlotOpacity = -1f;

    private const string SplatsBufferName = "GaussianSplats";
    private const string VisibleCountBufferName = "GaussianSplatVisibleCount";
    private const string OverflowBufferName = "GaussianSplatOverflow";
    private const string OrderBufferName = "GaussianSplatOrder";
    private const string ScratchBufferName = "GaussianSplatOrderScratch";

    [ThreadStatic]
    private static GaussianSplatRenderer? t_drawingRenderer;

    private readonly XRDataBuffer _order;
    private readonly XRDataBuffer _scratch;
    private readonly GpuKeyIdRadixSort _sort = new();
    private readonly XRMeshRenderer.DelSetUniforms _settingUniformsHandler;
    private XRMeshRenderer? _attachedQuad;
    private bool _hasOrder;
    private bool _disposed;

    /// <summary>Creates the GPU buffers for <paramref name="capacity"/> splats.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is less than 1 or more than <see cref="MaxCapacity"/>.</exception>
    public GaussianSplatRenderer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaxCapacity);
        Capacity = capacity;
        uint slots = (uint)capacity;

        Splats = new XRDataBuffer(SplatsBufferName, EBufferTarget.ShaderStorageBuffer, slots, EComponentType.Struct, SplatStrideBytes, false, false, allocateClientSideSource: false)
        {
            // Compute producers write the splats. Upload makes a CPU copy only when it is called.
            GpuProduced = true,
            BindingIndexOverride = SplatsBinding,
            Usage = EBufferUsage.DynamicDraw,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
        };
        VisibleCount = CreateCounter(VisibleCountBufferName, VisibleCountBinding);
        Overflow = CreateCounter(OverflowBufferName, OverflowBinding);
        _order = CreatePairs(OrderBufferName, slots, OrderBinding);
        _scratch = CreatePairs(ScratchBufferName, slots, null);
        _settingUniformsHandler = OnQuadSettingUniforms;
    }

    /// <summary>Number of splat slots in <see cref="Splats"/>.</summary>
    public int Capacity { get; }

    /// <summary>Storage buffer of <see cref="Capacity"/> <see cref="GaussianSplatGpuSplat"/> elements.</summary>
    public XRDataBuffer Splats { get; }

    /// <summary>
    /// Storage buffer of one unsigned integer: the number of valid splats at the start of <see cref="Splats"/>.
    /// Compute producers increment it atomically; values above <see cref="Capacity"/> count as <see cref="Capacity"/>.
    /// </summary>
    public XRDataBuffer VisibleCount { get; }

    /// <summary>Storage buffer of one unsigned integer that producers increment for each splat that does not fit.</summary>
    public XRDataBuffer Overflow { get; }

    /// <summary>Transform from the splat space (model space) to world space.</summary>
    public Matrix4x4 ModelMatrix { get; set; } = Matrix4x4.Identity;

    /// <summary>Multiplier for the standard deviation of every splat.</summary>
    public float SplatScale { get; set; } = 1f;

    /// <summary>
    /// When true, the producer writes every one of the <see cref="Capacity"/> slots in each frame: a splat, or an empty
    /// slot with the opacity <see cref="EmptySlotOpacity"/>. <see cref="Prepare(in Matrix4x4)"/> then sorts all slots, and
    /// <see cref="VisibleCount"/> is only the number of splats. Because the sort is stable, splats with equal depth keep the
    /// slot order, so a producer that writes a fixed slot for each splat gets the same draw order in each run.
    /// When false (the default), only the first <see cref="VisibleCount"/> slots hold splats.
    /// </summary>
    public bool AllSlotsWritten { get; set; }

    /// <summary>True after a <see cref="Prepare(in Matrix4x4)"/> call sorted the splats, so <see cref="Draw"/> can draw.</summary>
    public bool HasOrder => _hasOrder;

    /// <summary>Number of workgroups that the depth key shader dispatches for <paramref name="capacity"/> slots.</summary>
    public static uint GetKeyGroupCount(uint capacity)
        => Math.Max(1u, (capacity + KeysLocalSize - 1u) / KeysLocalSize);

    /// <summary>
    /// Copies <paramref name="splats"/> to the start of <see cref="Splats"/> and sets <see cref="VisibleCount"/>
    /// to their number. The first call makes a CPU copy of the splat buffer.
    /// </summary>
    /// <exception cref="ArgumentException">There are more splats than <see cref="Capacity"/>.</exception>
    public void Upload(ReadOnlySpan<GaussianSplatGpuSplat> splats)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (splats.Length > Capacity)
            throw new ArgumentException($"{splats.Length} splats do not fit in a renderer with capacity {Capacity}.", nameof(splats));

        Splats.EnsureRawCapacity<GaussianSplatGpuSplat>((uint)Capacity, growCapacityGeometrically: false);
        Splats.WriteDataRaw(splats);
        Splats.PushData();
        SetCounter(VisibleCount, (uint)splats.Length);
        SetCounter(Overflow, 0u);
    }

    /// <summary>Sets <see cref="VisibleCount"/> and <see cref="Overflow"/> to zero before producers write the splats of a frame.</summary>
    public void ResetCounters()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetCounter(VisibleCount, 0u);
        SetCounter(Overflow, 0u);
    }

    /// <summary>
    /// Sorts the splats for the current rendering camera. Call this in the PreRender pass.
    /// The method does nothing when there is no rendering camera.
    /// </summary>
    public void Prepare()
    {
        XRCamera? camera = RuntimeEngine.Rendering.State.RenderingCamera;
        if (camera is null)
            return;

        Matrix4x4 viewMatrix = camera.Transform.InverseRenderMatrix;
        Prepare(in viewMatrix);
    }

    /// <summary>
    /// Writes the depth key of each splat slot for <paramref name="viewMatrix"/> (world to view) and sorts
    /// the keys from far to near. Until the programs link, the method returns without a sort and
    /// <see cref="Draw"/> draws the last order (or nothing).
    /// </summary>
    public void Prepare(in Matrix4x4 viewMatrix)
    {
        if (_disposed || !EnsureKeysProgramReady() || !_sort.EnsureProgramsReady())
            return;

        XRRenderProgram keys = _keysProgram!;
        keys.BindBuffer(Splats, SplatsBinding);
        keys.BindBuffer(_order, OrderBinding);
        keys.BindBuffer(VisibleCount, VisibleCountBinding);
        keys.Uniform(CapacityUniform, (uint)Capacity);
        keys.Uniform(AllSlotsWrittenUniform, AllSlotsWritten ? 1u : 0u);
        keys.Uniform(ModelViewUniform, ModelMatrix * viewMatrix);
        keys.DispatchCompute(GetKeyGroupCount((uint)Capacity), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

        _sort.Sort(_order, _scratch, (uint)Capacity);
        _hasOrder = true;
    }

    /// <summary>
    /// Draws <see cref="Capacity"/> instances of <paramref name="quad"/> in sorted order. Call this in the
    /// TransparentForward pass after <see cref="Prepare()"/>.
    /// </summary>
    /// <param name="quad">A quad from <see cref="CreateQuadRenderer"/>. The renderer binds its buffers to the quad's program for this draw.</param>
    public void Draw(XRMeshRenderer quad)
    {
        ArgumentNullException.ThrowIfNull(quad);
        if (_disposed || !_hasOrder)
            return;

        AttachTo(quad);
        GaussianSplatRenderer? previous = t_drawingRenderer;
        t_drawingRenderer = this;
        try
        {
            Matrix4x4 model = ModelMatrix;
            // The quad shaders do not support single-pass stereo.
            quad.Render(model, model, null, (uint)Capacity, forceNoStereo: true);
        }
        finally
        {
            t_drawingRenderer = previous;
        }
    }

    /// <summary>Releases the GPU buffers and programs.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        AttachTo(null);
        _sort.Dispose();
        DisposePrograms();
        Splats.Dispose();
        VisibleCount.Dispose();
        Overflow.Dispose();
        _order.Dispose();
        _scratch.Dispose();
        _hasOrder = false;
    }

    /// <summary>
    /// Subscribes the binding handler to the quad that this renderer draws. A quad can be shared:
    /// the handler binds only during this renderer's <see cref="Draw"/> call.
    /// </summary>
    private void AttachTo(XRMeshRenderer? quad)
    {
        if (ReferenceEquals(_attachedQuad, quad))
            return;
        if (_attachedQuad is not null)
            _attachedQuad.SettingUniforms -= _settingUniformsHandler;
        _attachedQuad = quad;
        if (quad is not null)
            quad.SettingUniforms += _settingUniformsHandler;
    }

    private void OnQuadSettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
    {
        if (!ReferenceEquals(t_drawingRenderer, this))
            return;

        vertexProgram.BindBuffer(Splats, SplatsBinding);
        vertexProgram.BindBuffer(_order, OrderBinding);
        vertexProgram.BindBuffer(VisibleCount, VisibleCountBinding);
        vertexProgram.Uniform(SplatScaleUniform, SplatScale);
    }

    private static void SetCounter(XRDataBuffer counter, uint value)
    {
        counter.SetDataRawAtIndex(0u, value);
        counter.PushSubData();
    }

    private static XRDataBuffer CreateCounter(string name, uint binding)
        => new(name, EBufferTarget.ShaderStorageBuffer, 1u, EComponentType.UInt, 1u, false, true)
        {
            BindingIndexOverride = binding,
            Usage = EBufferUsage.DynamicCopy,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
        };

    private static XRDataBuffer CreatePairs(string name, uint slots, uint? binding)
        => new(name, EBufferTarget.ShaderStorageBuffer, slots * GpuKeyIdRadixSort.UIntsPerPair, EComponentType.UInt, 1u, false, true, allocateClientSideSource: false)
        {
            // The depth key shader writes every pair before the sort reads it.
            GpuProduced = true,
            BindingIndexOverride = binding,
            Usage = EBufferUsage.DynamicCopy,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
        };
}
