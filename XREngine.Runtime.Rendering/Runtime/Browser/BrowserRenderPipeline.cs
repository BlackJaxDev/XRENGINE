using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Explicit CPU-direct forward collection with reusable stable sorting, camera culling and painter-ordered UI.</summary>
public sealed class BrowserRenderPipeline : IDisposable
{
    private BrowserPipelineDraw[] _draws;
    private BrowserPipelineDraw[] _sortScratch;
    private BrowserPipelineUiQuad[] _ui;
    private int _drawCount;
    private int _uiCount;
    private bool _writing;
    private bool _disposed;

    public BrowserRenderPipeline(int initialDrawCapacity = 64, int initialUiCapacity = 32)
    {
        Packet = new BrowserPipelineFramePacket(initialDrawCapacity, initialUiCapacity);
        _draws = new BrowserPipelineDraw[initialDrawCapacity];
        _sortScratch = new BrowserPipelineDraw[initialDrawCapacity];
        _ui = new BrowserPipelineUiQuad[initialUiCapacity];
    }

    public BrowserPipelineFramePacket Packet { get; }

    public void EnsureCapacity(int draws, int ui)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writing) throw new InvalidOperationException("Pipeline capacity changes require an idle frame boundary.");
        Packet.EnsureCapacity(draws, ui);
        if (_draws.Length < Packet.DrawCapacity)
        {
            _draws = new BrowserPipelineDraw[Packet.DrawCapacity];
            _sortScratch = new BrowserPipelineDraw[Packet.DrawCapacity];
        }
        if (_ui.Length < Packet.UiCapacity) _ui = new BrowserPipelineUiQuad[Packet.UiCapacity];
    }

    public void Begin(int sessionId, int surfaceGeneration, int width, int height,
        in BrowserPipelineEnvironment environment, bool updateShadow = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writing) throw new InvalidOperationException("The preceding pipeline frame is incomplete.");
        Packet.Begin(sessionId, surfaceGeneration, width, height, environment, updateShadow);
        _drawCount = 0; _uiCount = 0; _writing = true;
    }

    public void AddDraw(in BrowserPipelineDraw draw)
    {
        RequireWriting();
        if (_drawCount == _draws.Length)
            throw new BrowserArenaCapacityException("focused draw", _drawCount + 1, _draws.Length, BrowserPipelineFramePacket.MaximumDraws);
        if (draw.AlphaMode is not ("opaque" or "masked" or "transparent") || !float.IsFinite(draw.ViewDepth))
            throw new ArgumentException("Draw requires a supported alpha mode and finite camera depth.", nameof(draw));
        _draws[_drawCount++] = draw;
    }

    /// <summary>Conservatively rejects an AABB only when every corner is outside one WebGPU clip plane.
    /// Shadow casters remain in the packet even when invisible to the color camera.</summary>
    public bool AddVisibleDraw(in BrowserPipelineDraw draw, Vector3 boundsMinimum, Vector3 boundsMaximum)
    {
        RequireWriting();
        bool visible = IsVisible(draw.Mvp, boundsMinimum, boundsMaximum);
        if (visible) AddDraw(draw);
        else if (draw.CastShadow) AddDraw(draw with { ShadowOnly = true });
        return visible;
    }

    public void AddUi(in BrowserPipelineUiQuad quad)
    {
        RequireWriting();
        if (_uiCount == _ui.Length)
            throw new BrowserArenaCapacityException("focused UI", _uiCount + 1, _ui.Length, BrowserPipelineFramePacket.MaximumUiQuads);
        _ui[_uiCount++] = quad;
    }

    public void Submit(IBrowserFocusedPipelineCapability renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        RequireWriting();
        try
        {
            StableSort();
            for (int i = 0; i < _drawCount; i++) Packet.AddDraw(_draws[i]);
            for (int i = 0; i < _uiCount; i++) Packet.AddUi(_ui[i]);
            Packet.Seal();
            renderer.SubmitPipelinePacket(Packet);
        }
        catch
        {
            Packet.Abort();
            throw;
        }
        finally { _writing = false; }
    }

    public void Abort() { ObjectDisposedException.ThrowIf(_disposed, this); Packet.Abort(); _writing = false; _drawCount = 0; _uiCount = 0; }
    public void Dispose()
    {
        if (_disposed) return;
        if (_writing) throw new InvalidOperationException("Abort the active frame before disposing its pipeline.");
        Packet.Dispose();
        _draws = Array.Empty<BrowserPipelineDraw>(); _sortScratch = Array.Empty<BrowserPipelineDraw>(); _ui = Array.Empty<BrowserPipelineUiQuad>();
        _disposed = true;
    }

    private void RequireWriting()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_writing) throw new InvalidOperationException("Begin a pipeline frame before collecting commands.");
    }

    private void StableSort()
    {
        for (int width = 1; width < _drawCount; width *= 2)
        {
            for (int start = 0; start < _drawCount; start += width * 2)
            {
                int middle = Math.Min(start + width, _drawCount), end = Math.Min(start + width * 2, _drawCount);
                int left = start, right = middle;
                for (int output = start; output < end; output++)
                    _sortScratch[output] = right >= end || (left < middle && Compare(_draws[left], _draws[right]) <= 0)
                        ? _draws[left++] : _draws[right++];
            }
            (_draws, _sortScratch) = (_sortScratch, _draws);
        }
    }

    private static int Compare(in BrowserPipelineDraw a, in BrowserPipelineDraw b)
    {
        // Group views first so transparency is ordered independently for each camera.
        int c = a.ViewportX.CompareTo(b.ViewportX); if (c != 0) return c;
        c = a.ViewportY.CompareTo(b.ViewportY); if (c != 0) return c;
        c = a.ViewportWidth.CompareTo(b.ViewportWidth); if (c != 0) return c;
        c = a.ViewportHeight.CompareTo(b.ViewportHeight); if (c != 0) return c;
        c = Rank(a.AlphaMode).CompareTo(Rank(b.AlphaMode)); if (c != 0) return c;
        if (a.AlphaMode == "transparent") return b.ViewDepth.CompareTo(a.ViewDepth);
        c = a.Material.Packed.CompareTo(b.Material.Packed); if (c != 0) return c;
        c = a.Mesh.Packed.CompareTo(b.Mesh.Packed); if (c != 0) return c;
        c = a.FirstIndex.CompareTo(b.FirstIndex); if (c != 0) return c;
        return a.IndexCount.CompareTo(b.IndexCount);
    }
    private static int Rank(string mode) => mode == "opaque" ? 0 : mode == "masked" ? 1 : 2;

    private static bool IsVisible(in Matrix4x4 mvp, Vector3 minimum, Vector3 maximum)
    {
        if (!BrowserPipelineFramePacket.Finite(mvp) || !float.IsFinite(minimum.X) || !float.IsFinite(minimum.Y) || !float.IsFinite(minimum.Z) ||
            !float.IsFinite(maximum.X) || !float.IsFinite(maximum.Y) || !float.IsFinite(maximum.Z) ||
            minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z)
            throw new ArgumentException("Culling requires finite ordered bounds and transform.");
        int outsideAll = 63;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector4 clip = Vector4.Transform(new Vector4((corner & 1) == 0 ? minimum.X : maximum.X,
                (corner & 2) == 0 ? minimum.Y : maximum.Y, (corner & 4) == 0 ? minimum.Z : maximum.Z, 1), mvp);
            int outside = (clip.X < -clip.W ? 1 : 0) | (clip.X > clip.W ? 2 : 0) |
                (clip.Y < -clip.W ? 4 : 0) | (clip.Y > clip.W ? 8 : 0) | (clip.Z < 0 ? 16 : 0) | (clip.Z > clip.W ? 32 : 0);
            outsideAll &= outside;
        }
        return outsideAll == 0;
    }
}
