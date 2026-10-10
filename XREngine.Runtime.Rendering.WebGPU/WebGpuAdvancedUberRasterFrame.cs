using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>
/// Full-float transient material results owned by one output family. The single
/// ordered renderer queue consumes each image before a later sample or frame
/// overwrites it; no history or readback consumer retains these contents.
/// </summary>
internal sealed class WebGpuAdvancedUberRasterFrame(WebGpuRendererHost renderer) : IDisposable
{
    private XRTexture2D? _depth;
    private uint _width, _height, _samples;
    private uint _sequence;
    private ulong _preparation;
    private uint _sample;
    internal XRTexture2DArray? Surface { get; private set; }
    internal XRFrameBuffer? Target { get; private set; }
    internal ulong ByteCount { get; private set; }

    internal void Prepare(uint width, uint height, uint samples, XRTexture2D depth)
    {
        if (samples is not (1 or 4) || width == 0 || height == 0)
            throw new NotSupportedException("WebGPU.Advanced.UberRasterExtent: full-resolution ordinary or four-sample storage is required.");
        ulong bytes = checked((ulong)width * height * 64u);
        // Mirrors the admitted executor's per-texture cap. Both sample profiles
        // cost 64 bytes per pixel per output, or 192 across three output families.
        // Four-sample rendering makes four complete export traversals, each
        // followed by all of that sample's consumers. No performance claim is
        // implied by sharing this exact image across samples and queued frames.
        if (bytes > 256ul * 1024 * 1024)
            throw new NotSupportedException($"WebGPU.Advanced.UberRasterImageBudget: full-resolution scratch requires {bytes} bytes; the executor allows 268435456 bytes per image.");
        if (Surface is null || _width != width || _height != height || _samples != samples || !ReferenceEquals(_depth, depth))
        {
            Dispose();
            Surface = XRTexture2DArray.CreateFrameBufferTexture(4u, width, height,
                EPixelInternalFormat.Rgba32f, EPixelFormat.Rgba, EPixelType.Float, EFrameBufferAttachment.ColorAttachment0);
            Surface.Name = "Advanced full-float Uber raster surfaces";
            Surface.SizedInternalFormat = ESizedInternalFormat.Rgba32f;
            Surface.RequiresStorageUsage = true;
            Surface.Resizable = false;
            Target = new((depth, EFrameBufferAttachment.DepthStencilAttachment, 0, -1));
            _width = width; _height = height; _samples = samples; _depth = depth; ByteCount = bytes;
        }
        // Allocation and views must become ready before any visibility commands.
        renderer.GetOrCreateAPIRenderObject(Surface, generateNow: true)!.Generate();
        renderer.GetOrCreateAPIRenderObject(Target!, generateNow: true)!.Generate();
    }

    internal void Invalidate() { _sequence = 0; _preparation = 0; _sample = uint.MaxValue; }
    internal void Commit(uint sequence, ulong preparation, uint sample) { _sequence = sequence; _preparation = preparation; _sample = sample; }
    internal bool IsCurrent(uint sequence, ulong preparation, uint sample)
        => Surface is not null && _sequence == sequence && _preparation == preparation && _sample == sample;

    public void Dispose()
    {
        Invalidate();
        // API owners defer their recorded physical generations until completion.
        Target?.Destroy(); Surface?.Destroy(); Target = null; Surface = null; _depth = null;
        _width = _height = _samples = 0; ByteCount = 0;
    }
}
