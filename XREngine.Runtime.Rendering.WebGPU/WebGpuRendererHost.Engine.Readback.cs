using System.Buffers.Binary;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Imaging;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    /// <summary>Canvas copy rows are already top-first; exporters must not flip them again.</summary>
    public override bool ScreenshotRequiresVerticalFlip => false;

    /// <summary>Captures the next complete presented engine frame without blocking the browser owner thread.</summary>
    public Task<RuntimeImage> CaptureCanvasAsync(BoundingRectangle region, bool withTransparency,
        CancellationToken cancellationToken = default)
    {
        RequireReady();
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryDescribeFrameOutput(out RenderFrameOutputDescription output))
            throw new InvalidOperationException("WebGPU.Readback.CanvasUnavailable: no drawable surface is configured.");
        int width = checked((int)output.Properties.Width);
        int height = checked((int)output.Properties.Height);
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            region.X > width || region.Y > height || region.Width > width - region.X ||
            region.Height > height - region.Y || (long)region.Width * region.Height > 4 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(region),
                "WebGPU.Readback.RegionUnsupported: the rectangle must fit the canvas and 16 MiB readback budget.");

        int session = _session;
        int generation = checked((int)output.TargetGeneration);
        int top = height - region.Y - region.Height;
        int ticket = WebGpuImports.BeginCanvasReadback(session, generation, region.X, top, region.Width, region.Height);
        return FinishCanvasCaptureAsync(session, generation, ticket, region.Width, region.Height,
            withTransparency, cancellationToken);
    }

    private async Task<RuntimeImage> FinishCanvasCaptureAsync(int session, int generation, int ticket,
        int width, int height, bool withTransparency, CancellationToken cancellationToken)
    {
        byte[] pixels = await FinishReadbackAsync(session, ticket, checked(width * height * 4), cancellationToken);
        if (!TryDescribeFrameOutput(out RenderFrameOutputDescription output) ||
            output.TargetGeneration != (ulong)generation)
            throw new InvalidOperationException("WebGPU.Readback.ObsoleteSurface: the canvas changed before capture completed.");
        // Browser canvas formats in this profile are unorm RGBA8 or BGRA8. These
        // are presentation-space bytes; no implicit linearization is performed.
        if (!withTransparency)
            ForceOpaque(pixels);
        return new RuntimeImage(checked((uint)width), checked((uint)height), RuntimePixelFormat.Rgba8, pixels);
    }

    public override void GetScreenshotAsync(BoundingRectangle region, bool withTransparency,
        Action<RuntimeImage, int> imageCallback)
    {
        ArgumentNullException.ThrowIfNull(imageCallback);
        Task<RuntimeImage> capture;
        try { capture = CaptureCanvasAsync(region, withTransparency); }
        catch (Exception error)
        {
            Debug.RenderingWarning("WebGPU.Readback.ScreenshotRejected: {0}", error.Message);
            imageCallback(null!, 0);
            return;
        }
        _ = CompleteLegacyScreenshotAsync(capture, imageCallback);
    }

    private static async Task CompleteLegacyScreenshotAsync(Task<RuntimeImage> capture,
        Action<RuntimeImage, int> callback)
    {
        RuntimeImage image;
        try { image = await capture; }
        catch (Exception error)
        {
            Debug.RenderingWarning("WebGPU.Readback.ScreenshotFailed: {0}", error.Message);
            try { callback(null!, 0); }
            catch (Exception callbackError) { Debug.RenderingWarning("WebGPU.Readback.ScreenshotCallbackFailed: {0}", callbackError.Message); }
            return;
        }
        // Ownership transfers with this invocation, even when consumer code throws.
        try { callback(image, checked((int)((long)image.Width * image.Height))); }
        catch (Exception error) { Debug.RenderingWarning("WebGPU.Readback.ScreenshotCallbackFailed: {0}", error.Message); }
    }

    public override bool TryQueueScreenshotReadback(BoundingRectangle region, bool withTransparency,
        Action<ScreenshotReadbackResult> callback, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Task<RuntimeImage> capture;
        try { capture = CaptureCanvasAsync(region, withTransparency); }
        catch (Exception error)
        {
            failure = error.Message;
            return false;
        }
        failure = null;
        _ = CompleteStructuredScreenshotAsync(capture, callback);
        return true;
    }

    private static async Task CompleteStructuredScreenshotAsync(Task<RuntimeImage> capture,
        Action<ScreenshotReadbackResult> callback)
    {
        ScreenshotReadbackResult result;
        try
        {
            RuntimeImage image = await capture;
            result = ScreenshotReadbackResult.Success(image, checked((int)((long)image.Width * image.Height)),
                checked((int)image.Width), checked((int)image.Height), "WebGPU", "rgba8unorm",
                image.Pixels.Length);
        }
        catch (Exception error)
        {
            Debug.RenderingWarning("WebGPU.Readback.ScreenshotFailed: {0}", error.Message);
            result = ScreenshotReadbackResult.Failure(error.Message, "WebGPU");
        }
        try { callback(result); }
        catch (Exception error) { Debug.RenderingWarning("WebGPU.Readback.ScreenshotCallbackFailed: {0}", error.Message); }
    }

    /// <summary>Samples the next complete canvas frame at engine bottom-left coordinates.</summary>
    public Task<ColorF4> ReadCanvasPixelAsync(int x, int y, bool withTransparency,
        CancellationToken cancellationToken = default)
        => ReadCanvasPixelCoreAsync(x, y, withTransparency, cancellationToken);

    private async Task<ColorF4> ReadCanvasPixelCoreAsync(int x, int y, bool withTransparency,
        CancellationToken cancellationToken)
    {
        using RuntimeImage image = await CaptureCanvasAsync(new BoundingRectangle(x, y, 1, 1),
            withTransparency, cancellationToken);
        ReadOnlySpan<byte> pixel = image.Pixels.Span;
        return new ColorF4(pixel[0] / 255f, pixel[1] / 255f, pixel[2] / 255f, pixel[3] / 255f);
    }

    public override void GetPixelAsync(int x, int y, bool withTransparency, Action<ColorF4> colorCallback)
    {
        ArgumentNullException.ThrowIfNull(colorCallback);
        _ = CompletePixelAsync(ReadCanvasPixelAsync(x, y, withTransparency), colorCallback);
    }

    private static async Task CompletePixelAsync(Task<ColorF4> readback, Action<ColorF4> callback)
    {
        ColorF4 color;
        try { color = await readback; }
        catch (Exception error)
        {
            Debug.RenderingWarning("WebGPU.Readback.PixelFailed: {0}", error.Message);
            color = ColorF4.Transparent;
        }
        try { callback(color); }
        catch (Exception error) { Debug.RenderingWarning("WebGPU.Readback.PixelCallbackFailed: {0}", error.Message); }
    }

    /// <summary>Reads an already committed single-sample depth32float framebuffer pixel.</summary>
    public Task<float> ReadEngineDepthAsync(XRFrameBuffer framebuffer, int x, int y,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(framebuffer);
        RequireReady();
        cancellationToken.ThrowIfCancellationRequested();
        if (_engineRecording)
            throw new InvalidOperationException("WebGPU.Readback.InFrameDepthUnsupported: framebuffer reads during frame recording would sample stale contents.");
        WebGpuFrameBuffer wrapper = (WebGpuFrameBuffer)GetOrCreateAPIRenderObject(framebuffer, generateNow: true)!;
        wrapper.EnsureCurrent();
        foreach (var target in framebuffer.Targets ?? [])
        {
            if (target.Attachment != EFrameBufferAttachment.DepthAttachment) continue;
            if (target.Target is not XRTexture2D texture || target.LayerIndex != -1 ||
                target.MipLevel < 0 || texture.SizedInternalFormat != ESizedInternalFormat.DepthComponent32f)
                throw UnsupportedEngineOperation(nameof(ReadEngineDepthAsync),
                    "depth readback requires a single-layer depth32float 2D attachment");
            WebGpuTexture2D api = (WebGpuTexture2D)GetOrCreateAPIRenderObject(texture, generateNow: true)!;
            if (api.SampleCount != 1 || !api.HasCommittedProduction)
                throw new InvalidOperationException("WebGPU.Readback.DepthNotCommitted: the single-sample depth attachment has no accepted producer frame.");
            int width = checked((int)Math.Max(1u, api.Width >> target.MipLevel));
            int height = checked((int)Math.Max(1u, api.Height >> target.MipLevel));
            int boundedX = Math.Clamp(x, 0, width - 1);
            int boundedY = Math.Clamp(y, 0, height - 1);
            return ReadDepthPixelCoreAsync(api.ResourceHandle, target.MipLevel, boundedX,
                height - boundedY - 1, width, height, cancellationToken);
        }
        throw UnsupportedEngineOperation(nameof(ReadEngineDepthAsync), "the framebuffer has no depth attachment");
    }

    private async Task<float> ReadDepthPixelCoreAsync(int handle, int mip, int x, int top, int width, int height,
        CancellationToken cancellationToken)
    {
        if ((long)width * height > 4 * 1024 * 1024)
            throw new NotSupportedException("WebGPU.Readback.DepthMipTooLarge: the full depth mip exceeds the 16 MiB readback limit.");
        byte[] bytes = await ReadTextureAsync(new BrowserTextureReadbackDescription(handle, mip, 0, 0, width, height),
            cancellationToken);
        int offset = checked((top * width + x) * sizeof(float));
        return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(float))));
    }

    public override void GetDepthAsync(XRFrameBuffer fbo, int x, int y, Action<float> depthCallback)
    {
        ArgumentNullException.ThrowIfNull(depthCallback);
        Task<float> readback;
        try { readback = ReadEngineDepthAsync(fbo, x, y); }
        catch (Exception error)
        {
            Debug.RenderingWarning("WebGPU.Readback.DepthRejected: {0}", error.Message);
            try { depthCallback(1f); }
            catch (Exception callbackError) { Debug.RenderingWarning("WebGPU.Readback.DepthCallbackFailed: {0}", callbackError.Message); }
            return;
        }
        _ = CompleteDepthAsync(readback, depthCallback);
    }

    private static async Task CompleteDepthAsync(Task<float> readback, Action<float> callback)
    {
        float depth;
        try { depth = await readback; }
        catch (Exception error)
        {
            Debug.RenderingWarning("WebGPU.Readback.DepthFailed: {0}", error.Message);
            depth = 1f;
        }
        try { callback(depth); }
        catch (Exception error) { Debug.RenderingWarning("WebGPU.Readback.DepthCallbackFailed: {0}", error.Message); }
    }

    private static void ForceOpaque(Span<byte> pixels)
    {
        for (int index = 3; index < pixels.Length; index += 4)
            pixels[index] = 255;
    }
}
