using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>A browser-owned canvas presentation target with host-supplied surface and input snapshots.</summary>
public sealed class BrowserCanvasRenderTarget : IBrowserCanvasPresentationTarget
{
    private double? _lastFrameTimestampMilliseconds;
    private string? _colorEncoding;

    public BrowserCanvasRenderTarget(string canvasId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canvasId);
        CanvasId = canvasId;
    }

    /// <summary>The identifier used by the browser host to locate its canvas.</summary>
    public string CanvasId { get; }

    public RenderExecutionMode ExecutionMode => RenderExecutionMode.BrowserCanvas;

    public RendererBackendCapabilities RequiredBackendCapabilities =>
        RendererBackendCapabilities.BrowserCanvasPresentation;

    /// <summary>The host-reported drawable canvas extent and exact configured format.</summary>
    public RenderTargetOutputProperties? OutputProperties =>
        Surface.CanRender && Surface.Generation > 0 && _colorEncoding is not null
            ? new RenderTargetOutputProperties(
                (uint)Surface.PhysicalWidth,
                (uint)Surface.PhysicalHeight,
                Layers: 1,
                ColorFormat: EPixelInternalFormat.Rgba8,
                DepthFormat: EPixelInternalFormat.DepthComponent24,
                ColorSpace: "sRGB",
                SampleCount: 1,
                FrameSlotCount: 1)
            {
                ColorEncoding = _colorEncoding,
                DepthEncoding = "depth24plus",
            }
            : null;

    /// <summary>Sets the exact canvas encoding selected and configured by the browser host.</summary>
    public void SetColorFormat(string format)
    {
        if (format is not ("rgba8unorm" or "bgra8unorm"))
            throw new ArgumentOutOfRangeException(nameof(format), "The canvas requires rgba8unorm or bgra8unorm.");
        if (_colorEncoding is not null && _colorEncoding != format)
            throw new InvalidOperationException("A canvas session cannot change its configured color format.");
        _colorEncoding = format;
    }

    /// <summary>Describes a drawable canvas output without exposing its acquired GPU texture.</summary>
    public bool TryDescribeFrameOutput(out RenderFrameOutputDescription output)
    {
        if (OutputProperties is not { } properties)
        {
            output = default;
            return false;
        }

        output = new RenderFrameOutputDescription(
            ExecutionMode,
            properties,
            (ulong)Surface.Generation,
            FrameSlotIndex: 0,
            Capabilities: RenderFrameOutputCapabilities.Presentation | RenderFrameOutputCapabilities.IndependentSceneSamples);
        return true;
    }

    public RuntimeSurfaceState Surface { get; private set; } = new(0, 0, 0, 0, 1, 0, false, false, false);

    public RuntimeInputState Input { get; private set; }

    public void Validate() => ArgumentException.ThrowIfNullOrWhiteSpace(CanvasId);

    public void UpdateSurface(RuntimeSurfaceState surface)
    {
        surface.Validate();
        if (!surface.CanRender || surface.Generation != Surface.Generation)
            ResetFrameClock();
        Surface = surface;
    }

    public void UpdateInput(RuntimeInputState input)
    {
        input.Validate();
        Input = input;
    }

    public void ResetFrameClock() => _lastFrameTimestampMilliseconds = null;

    public bool TryBeginFrame(double timestampMilliseconds, double minimumFrameIntervalMilliseconds, out double deltaSeconds)
    {
        deltaSeconds = 0;
        if (!double.IsFinite(timestampMilliseconds) ||
            !double.IsFinite(minimumFrameIntervalMilliseconds) || minimumFrameIntervalMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timestampMilliseconds), "Frame timestamps and intervals must be finite; intervals must be non-negative.");

        if (!Surface.CanRender)
        {
            ResetFrameClock();
            return false;
        }

        if (_lastFrameTimestampMilliseconds is double previous)
        {
            double elapsed = timestampMilliseconds - previous;
            if (elapsed < 0)
            {
                ResetFrameClock();
            }
            else
            {
                if (elapsed < minimumFrameIntervalMilliseconds)
                    return false;
                deltaSeconds = elapsed / 1000.0;
            }
        }

        _lastFrameTimestampMilliseconds = timestampMilliseconds;
        return true;
    }
}
