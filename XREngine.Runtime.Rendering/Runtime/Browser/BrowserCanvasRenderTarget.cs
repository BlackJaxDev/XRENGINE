namespace XREngine.Rendering;

/// <summary>A browser-owned canvas presentation target with host-supplied surface and input snapshots.</summary>
public sealed class BrowserCanvasRenderTarget : IRendererPresentationTarget, IRuntimeSurfaceHost
{
    private double? _lastFrameTimestampMilliseconds;

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

    /// <summary>The live canvas, rather than this target, owns its extent and format.</summary>
    public RenderTargetOutputProperties? OutputProperties => null;

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
