using System.Numerics;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Browser;

/// <summary>Owns one canvas, scene lifecycle, fixed simulation clock, and browser frame submission.</summary>
public sealed class BrowserSceneSession : IDisposable
{
    private const double FixedStep = 1.0 / 60.0;
    private const int MaxStepsPerFrame = 4;

    private readonly RuntimeSceneHost _host;
    private readonly SceneNode _triangle;
    private BrowserViewport _left;
    private BrowserViewport _right;
    private bool _splitView;
    private double _accumulator;
    private bool _disposed;

    public BrowserSceneSession(int id, string canvasId)
    {
        Id = id;
        Target = new BrowserCanvasRenderTarget(canvasId);
        _host = new RuntimeSceneHost();
        try
        {
            _triangle = new SceneNode("BrowserTriangle", new Transform(new Vector3(0, 0, -2.5f)));
            _host.RootNodes.Add(_triangle);
            BrowserSpinComponent component = _triangle.AddComponent(static () => new BrowserSpinComponent())
                ?? throw new InvalidOperationException("Browser scene component creation failed.");
            component.Target = Target;
            _host.Start();
        }
        catch
        {
            _host.Dispose();
            Data.Core.XRObjectBase.ProcessPendingDestructions();
            throw;
        }
    }

    public int Id { get; }
    public BrowserCanvasRenderTarget Target { get; }

    public void Resize(RuntimeSurfaceState surface)
    {
        ThrowIfDisposed();
        RuntimeSurfaceState previous = Target.Surface;
        Target.UpdateSurface(surface);
        if (!surface.CanRender || surface.Generation != previous.Generation)
            _accumulator = 0;
        if (surface.PhysicalWidth != previous.PhysicalWidth || surface.PhysicalHeight != previous.PhysicalHeight)
            RebuildViews();
    }

    public void Input(RuntimeInputState input)
    {
        ThrowIfDisposed();
        Target.UpdateInput(input);
    }

    public void SetSplitView(bool split)
    {
        ThrowIfDisposed();
        if (_splitView == split)
            return;
        _splitView = split;
        RebuildViews();
    }

    public void ResetClock()
    {
        ThrowIfDisposed();
        Target.ResetFrameClock();
        _accumulator = 0;
    }

    public void Frame(double timestampMilliseconds)
    {
        ThrowIfDisposed();
        if (!Target.TryBeginFrame(timestampMilliseconds, 0, out double elapsed))
            return;

        // Drop excess elapsed time instead of attempting an unbounded simulation catch-up.
        _accumulator = Math.Min(_accumulator + Math.Min(elapsed, FixedStep * MaxStepsPerFrame),
            FixedStep * MaxStepsPerFrame);
        int steps = 0;
        try
        {
            while (_accumulator >= FixedStep && steps < MaxStepsPerFrame)
            {
                _host.Advance((float)FixedStep);
                _accumulator -= FixedStep;
                steps++;
            }

            // Deferred scene destruction is drained by the application frame boundary.
            Data.Core.XRObjectBase.ProcessPendingDestructions();

            try
            {
                if (!BrowserSceneExports.BeginFrame(Id, Target.Surface.Generation))
                    return;
                Draw(0, _left);
                if (_splitView && _right.Width > 0)
                    Draw(1, _right);
                BrowserSceneExports.EndFrame(Id);
            }
            catch
            {
                BrowserSceneExports.AbortFrame(Id);
                throw;
            }
        }
        finally
        {
            Data.Core.XRObjectBase.ProcessPendingDestructions();
        }
    }

    private void Draw(int viewIndex, BrowserViewport viewport)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0)
            return;

        // System.Numerics uses row vectors. Uploading row-major fields as WGSL
        // columns represents the transpose needed for column-vector multiplication.
        Matrix4x4 matrix = _triangle.Transform.RenderMatrix * viewport.ViewProjection;
        BrowserSceneExports.Draw(Id, viewIndex, viewport.X, viewport.Y, viewport.Width, viewport.Height,
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44);
    }

    private void RebuildViews()
    {
        RuntimeSurfaceState surface = Target.Surface;
        int width = surface.PhysicalWidth;
        int height = surface.PhysicalHeight;
        if (!_splitView)
        {
            _left = BrowserViewport.Create(0, 0, width, height, Matrix4x4.Identity);
            _right = default;
            return;
        }

        int leftWidth = width / 2;
        _left = BrowserViewport.Create(0, 0, leftWidth, height, Matrix4x4.Identity);
        _right = BrowserViewport.Create(leftWidth, 0, width - leftWidth, height,
            Matrix4x4.CreateLookAt(new Vector3(1.5f, 0.8f, 0), new Vector3(0, 0, -2.5f), Vector3.UnitY));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _host.Dispose();
        Data.Core.XRObjectBase.ProcessPendingDestructions();
    }
}
