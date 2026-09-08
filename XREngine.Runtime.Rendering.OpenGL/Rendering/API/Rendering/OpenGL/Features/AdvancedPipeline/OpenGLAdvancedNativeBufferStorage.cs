using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

/// <summary>Slot-owned storage for the native Advanced compute bindings. Its lifetime is fenced by the visibility output slot.</summary>
internal sealed unsafe class OpenGLAdvancedNativeBufferStorage : IDisposable
{
    private const uint MaxKernels = 128u;
    private const uint PushRecordsPerView = MaxKernels + 1u;
    private const uint MaxLightIndices = 1_048_576u;
    private const uint MaxDecalIndices = 65_536u;
    private const uint UniformBufferOffsetAlignment = 0x8A34u;
    private static readonly uint[] Bindings = [79u, 80u, 81u, 82u, 84u, 85u, 86u, 87u, 88u, 89u];
    private readonly OpenGLRenderer _renderer;
    private readonly uint[] _buffers = new uint[Bindings.Length];
    private uint _pushBuffer;
    private uint[] _pushWords = [];
    internal ulong PreparedRenderFrame { get; set; }
    internal ulong PreparedPublication { get; set; }
    private nuint _pushStride;
    private nuint _pushBytes;
    private ulong _capacityTiles;
    private uint _capacityDepthSlices;
    private uint _capacityViews;

    internal OpenGLAdvancedNativeBufferStorage(OpenGLRenderer renderer) => _renderer = renderer;

    internal void Bind()
    {
        for (int index = 0; index < _buffers.Length; ++index)
        {
            if (_buffers[index] == 0u) _buffers[index] = _renderer.RawGL.CreateBuffer();
            _renderer.RawGL.BindBufferBase(GLEnum.ShaderStorageBuffer, Bindings[index], _buffers[index]);
        }
    }

    internal void EnsureCapacity(uint width, uint height, uint viewCount, uint depthSlices)
    {
        uint tilesX = DivideRoundUp(width, 16u), tilesY = DivideRoundUp(height, 16u);
        ulong totalTiles = checked((ulong)tilesX * tilesY * Math.Max(1u, viewCount));
        if (totalTiles <= _capacityTiles && depthSlices <= _capacityDepthSlices && viewCount <= _capacityViews) return;
        // The containing slot is acquired only after its previous submission fence completes.
        _capacityTiles = Math.Max(_capacityTiles, totalTiles);
        _capacityDepthSlices = Math.Max(_capacityDepthSlices, Math.Max(1u, depthSlices));
        _capacityViews = Math.Max(_capacityViews, Math.Max(1u, viewCount));
        ReadOnlySpan<nuint> bytes =
        [
            CheckedBytes(_capacityTiles, 16u), CheckedBytes(_capacityTiles, MaxKernels * 16u), 32u,
            MaxKernels * 16u, MaxKernels * 4u, CheckedBytes(checked(_capacityTiles * _capacityDepthSlices), 16u),
            MaxLightIndices * 4u, 16u, CheckedBytes(checked(_capacityTiles * _capacityDepthSlices), 8u), MaxDecalIndices * 4u,
        ];
        for (int index = 0; index < _buffers.Length; ++index)
            _renderer.RawGL.NamedBufferData(_buffers[index], bytes[index], null, GLEnum.DynamicDraw);
    }

    internal void UploadPushConstants(uint width, uint height, uint viewCount, uint depthSlices, uint lightCount,
        bool requireNativeOutput, bool enableAmbientOcclusion, bool enableIbl, EAdvancedShadingDebugView debugView)
    {
        uint alignment = checked((uint)Math.Max(64, _renderer.RawGL.GetInteger((GLEnum)UniformBufferOffsetAlignment)));
        _pushStride = AlignUp(64u, alignment);
        nuint requiredBytes = checked((nuint)Math.Max(1u, viewCount) * PushRecordsPerView * _pushStride);
        if (_pushBuffer == 0u) _pushBuffer = _renderer.RawGL.CreateBuffer();
        if (_pushBytes < requiredBytes)
        {
            _renderer.RawGL.NamedBufferData(_pushBuffer, requiredBytes, null, GLEnum.StreamDraw);
            _pushBytes = requiredBytes;
            _pushWords = new uint[checked((int)(requiredBytes / sizeof(uint)))];
        }
        uint tilesX = DivideRoundUp(width, 16u), tilesY = DivideRoundUp(height, 16u);
        uint flags = (requireNativeOutput ? 2u : 0u) | (enableAmbientOcclusion ? 4u : 0u) |
            (enableIbl ? 8u : 0u) | (((uint)debugView & 0xFFu) << 8);
        Span<uint> words = stackalloc uint[16];
        words[0] = width; words[1] = height; words[2] = tilesX; words[3] = tilesY;
        words[5] = viewCount; words[7] = flags; words[8] = depthSlices; words[9] = MaxLightIndices;
        words[10] = lightCount; words[11] = checked((uint)Math.Min(uint.MaxValue, (ulong)tilesX * tilesY * Math.Max(1u, viewCount) * MaxKernels));
        var clear = RuntimeEngine.StartupPresentationClearColor;
        words[12] = BitConverter.SingleToUInt32Bits(clear.R);
        words[13] = BitConverter.SingleToUInt32Bits(clear.G);
        words[14] = BitConverter.SingleToUInt32Bits(clear.B);
        words[15] = BitConverter.SingleToUInt32Bits(clear.A);
        for (uint view = 0u; view < Math.Max(1u, viewCount); ++view)
            for (uint kernel = 0u; kernel < PushRecordsPerView; ++kernel)
            {
                words[4] = view;
                words[6] = kernel == MaxKernels ? 0u : kernel;
                words[7] = flags | (kernel == MaxKernels ? 1u : 0u);
                words.CopyTo(_pushWords.AsSpan(checked((int)(PushOffset(view, kernel) / sizeof(uint))), 16));
            }
        fixed (uint* data = _pushWords)
            _renderer.RawGL.NamedBufferSubData(_pushBuffer, 0, requiredBytes, data);
    }

    internal void BindPushConstants(uint view, uint kernel, bool overflowRepair = false)
    {
        _renderer.RawGL.BindBufferRange(GLEnum.UniformBuffer, 1u, _pushBuffer,
            checked((nint)PushOffset(view, overflowRepair ? MaxKernels : kernel)), 64u);
    }
    internal void BindDispatchArguments() => _renderer.RawGL.BindBuffer(GLEnum.DispatchIndirectBuffer, _buffers[3]);
    internal unsafe void ResetClassification()
    {
        uint zero = 0u;
        _renderer.RawGL.ClearNamedBufferData(_buffers[2], GLEnum.R32ui, GLEnum.RedInteger, GLEnum.UnsignedInt, &zero);
        _renderer.RawGL.ClearNamedBufferData(_buffers[4], GLEnum.R32ui, GLEnum.RedInteger, GLEnum.UnsignedInt, &zero);
    }
    internal unsafe void ResetLightingCounters()
    {
        uint zero = 0u;
        _renderer.RawGL.ClearNamedBufferData(_buffers[7], GLEnum.R32ui, GLEnum.RedInteger, GLEnum.UnsignedInt, &zero);
    }
    public void Dispose()
    {
        foreach (uint buffer in _buffers) if (buffer != 0u && RuntimeEngine.IsRenderThread) _renderer.RawGL.DeleteBuffer(buffer);
        if (_pushBuffer != 0u && RuntimeEngine.IsRenderThread) _renderer.RawGL.DeleteBuffer(_pushBuffer);
    }
    private nuint PushOffset(uint view, uint kernel) => checked(((nuint)view * PushRecordsPerView + kernel) * _pushStride);
    private static uint DivideRoundUp(uint value, uint divisor) => Math.Max(1u, checked((value + divisor - 1u) / divisor));
    private static nuint AlignUp(uint value, uint alignment) => checked((nuint)((value + alignment - 1u) / alignment * alignment));
    private static nuint CheckedBytes(ulong elements, uint stride) => checked((nuint)checked(elements * stride));
}
