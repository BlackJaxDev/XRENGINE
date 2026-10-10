using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>
/// Borrows a byte range of a WebGPU buffer. WebGPU has no separate buffer-view resource;
/// consumers must bind the returned buffer handle together with this range. The authored
/// sized format is retained as metadata, not a sampled texture-buffer view or a promise
/// of shader interpretation. A consuming binding must validate its cooked shader layout.
/// </summary>
public sealed class WebGpuDataBufferView(WebGpuRendererHost renderer, XRDataBufferView data)
    : WebGpuObject<XRDataBufferView>(renderer, data)
{
    private WebGpuDataBuffer? _source;
    private XRDataBuffer? _buffer;
    private ESizedInternalFormat _format;
    private uint _offset;
    private uint _length;
    private uint _stride;
    private uint _alignment;
    private uint _sourceLength;
    private int _sourceHandle;

    public override bool IsGenerated =>
        !IsRetired && !Data.IsDestroyed && !Data.Buffer.IsDestroyed &&
        _source is { IsGenerated: true } &&
        ReferenceEquals(_buffer, Data.Buffer) &&
        _sourceHandle == _source.ResourceHandle &&
        _sourceLength == Data.Buffer.Length &&
        _format == Data.InternalFormat &&
        _offset == Data.OffsetBytes &&
        _length == Data.EffectiveLengthBytes &&
        _stride == Data.StrideBytes &&
        _alignment == Data.AlignmentBytes;

    /// <summary>The borrowed physical buffer handle; pair it with <see cref="OffsetBytes"/> and <see cref="LengthBytes"/>.</summary>
    public int ResourceHandle => IsGenerated ? _sourceHandle : 0;
    public override nint GetHandle() => ResourceHandle;
    public uint OffsetBytes => IsGenerated ? _offset : throw UnavailableRange();
    public uint LengthBytes => IsGenerated ? _length : throw UnavailableRange();
    public uint StrideBytes => IsGenerated ? _stride : throw UnavailableRange();
    public ESizedInternalFormat InternalFormat => Data.InternalFormat;

    public override void Generate()
    {
        ValidateOwnerGeneration();
        if (IsRetired || Data.IsDestroyed || Data.Buffer.IsDestroyed)
            throw Unsupported("a destroyed or retired buffer view cannot regenerate");
        if (IsGenerated)
            return;

        XRDataBuffer buffer = Data.Buffer;
        uint offset = Data.OffsetBytes;
        uint length = Data.EffectiveLengthBytes;
        uint stride = Data.StrideBytes;
        uint alignment = Data.AlignmentBytes;
        ESizedInternalFormat format = Data.InternalFormat;
        if (offset >= buffer.Length || length == 0)
            throw Unsupported("the authored byte range is empty or begins outside the source buffer");
        if (alignment == 0 || offset % alignment != 0)
            throw Unsupported("the authored byte offset does not satisfy its declared alignment");

        uint formatBytes = GetFormatByteWidth(format);
        if (stride < formatBytes || length < stride)
            throw Unsupported("the byte range and stride must contain at least one complete formatted element");

        WebGpuDataBuffer source = (WebGpuDataBuffer)Renderer.GetOrCreateAPIRenderObject(buffer, generateNow: true)!;
        source.Generate();
        if (!source.BackendIsReadyForGpuUse || source.ResourceHandle == 0)
            throw Unsupported("the source buffer is not ready for GPU use");

        SetField(ref _source, source, publishNotifications: false);
        SetField(ref _buffer, buffer, publishNotifications: false);
        SetField(ref _format, format, publishNotifications: false);
        SetField(ref _offset, offset, publishNotifications: false);
        SetField(ref _length, length, publishNotifications: false);
        SetField(ref _stride, stride, publishNotifications: false);
        SetField(ref _alignment, alignment, publishNotifications: false);
        SetField(ref _sourceLength, buffer.Length, publishNotifications: false);
        SetField(ref _sourceHandle, source.ResourceHandle, publishNotifications: false);
    }

    public override void Destroy()
    {
        // The XRDataBuffer wrapper owns the allocation and its retirement.
        SetField(ref _source, null, publishNotifications: false);
        SetField(ref _buffer, null, publishNotifications: false);
        SetField(ref _sourceHandle, 0, publishNotifications: false);
    }

    private static uint GetFormatByteWidth(ESizedInternalFormat format) => format switch
    {
        ESizedInternalFormat.R8 or ESizedInternalFormat.R8Snorm or ESizedInternalFormat.R8i or
            ESizedInternalFormat.R8ui => 1,
        ESizedInternalFormat.R16 or ESizedInternalFormat.R16Snorm or ESizedInternalFormat.R16f or
            ESizedInternalFormat.R16i or ESizedInternalFormat.R16ui or ESizedInternalFormat.Rg8 or
            ESizedInternalFormat.Rg8Snorm or ESizedInternalFormat.Rg8i or ESizedInternalFormat.Rg8ui => 2,
        ESizedInternalFormat.Rgb8 or ESizedInternalFormat.Rgb8Snorm or ESizedInternalFormat.Srgb8 or
            ESizedInternalFormat.Rgb8i or ESizedInternalFormat.Rgb8ui => 3,
        ESizedInternalFormat.R32f or ESizedInternalFormat.R32i or ESizedInternalFormat.R32ui or
            ESizedInternalFormat.Rg16 or ESizedInternalFormat.Rg16Snorm or ESizedInternalFormat.Rg16f or
            ESizedInternalFormat.Rg16i or ESizedInternalFormat.Rg16ui or ESizedInternalFormat.Rgba8 or
            ESizedInternalFormat.Rgba8Snorm or ESizedInternalFormat.Srgb8Alpha8 or
            ESizedInternalFormat.Rgba8i or ESizedInternalFormat.Rgba8ui or ESizedInternalFormat.Rgb10A2 or
            ESizedInternalFormat.R11fG11fB10f or ESizedInternalFormat.Rgb9E5 => 4,
        ESizedInternalFormat.Rgb16Snorm or ESizedInternalFormat.Rgb16f or
            ESizedInternalFormat.Rgb16i or ESizedInternalFormat.Rgb16ui => 6,
        ESizedInternalFormat.Rg32f or ESizedInternalFormat.Rg32i or ESizedInternalFormat.Rg32ui or
            ESizedInternalFormat.Rgba16 or ESizedInternalFormat.Rgba16f or
            ESizedInternalFormat.Rgba16i or ESizedInternalFormat.Rgba16ui => 8,
        ESizedInternalFormat.Rgb32f or ESizedInternalFormat.Rgb32i or ESizedInternalFormat.Rgb32ui => 12,
        ESizedInternalFormat.Rgba32f or ESizedInternalFormat.Rgba32i or ESizedInternalFormat.Rgba32ui => 16,
        _ => throw Unsupported($"sized format '{format}' has no unambiguous byte width in this buffer-range profile"),
    };

    private static InvalidOperationException UnavailableRange()
        => new("WebGPU.BufferView.RangeUnavailable: generate the current view before using its byte range.");

    private static NotSupportedException Unsupported(string reason)
        => new($"WebGPU.BufferView.OperationUnsupported: {reason}.");
}
