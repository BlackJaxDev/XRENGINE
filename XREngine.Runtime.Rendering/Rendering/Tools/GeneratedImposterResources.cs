using XREngine.Data;

namespace XREngine.Rendering.Tools;

/// <summary>Owns the exact resources created by one completed capture, independent of later asset bindings.</summary>
internal sealed class GeneratedImposterResources : IDisposable
{
    private readonly OctahedralBillboardAsset _asset;
    private readonly XRTexture2DArray[] _arrays;
    private readonly XRTexture2D[] _slices;
    private readonly (Mipmap2D Mip, DataSource Pixels)[] _pixels;
    private XRTexture[] _previews;
    private bool _disposed;

    internal GeneratedImposterResources(OctahedralImposterGenerator.Result result)
    {
        _asset = result.Asset;
        _arrays = result.DepthViews is { } depth ? [result.Views, depth] : [result.Views];
        List<XRTexture2D> slices = [];
        List<(Mipmap2D, DataSource)> pixels = [];
        foreach (XRTexture2DArray array in _arrays)
            foreach (XRTexture2D slice in array.Textures)
            {
                slices.Add(slice);
                foreach (Mipmap2D mip in slice.Mipmaps)
                    if (mip.Data is { } data) pixels.Add((mip, data));
            }
        _slices = [.. slices];
        _pixels = [.. pixels];
        _previews = [.. result.PreviewTextures];
    }

    internal void DisposePreviews()
    {
        XRTexture[] previews = _previews;
        _previews = [];
        List<Exception>? failures = null;
        foreach (XRTexture preview in previews) Release(() => preview.Destroy(), ref failures);
        if (failures is not null) throw new AggregateException("Generated impostor preview cleanup failed.", failures);
    }

    internal bool Matches(OctahedralBillboardAsset? asset, XRTexture2DArray? views)
        => !_disposed && ReferenceEquals(asset, _asset) && ReferenceEquals(views, _arrays[0]);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? failures = null;
        Release(DisposePreviews, ref failures);
        foreach (XRTexture2DArray array in _arrays) Release(() => array.Destroy(), ref failures);
        foreach (var (mip, pixels) in _pixels)
        {
            // A consumer can replace a mip or bind a different authored asset. Those replacements are borrowed.
            if (ReferenceEquals(mip.Data, pixels)) Release(() => mip.Data = null, ref failures);
            Release(pixels.Dispose, ref failures);
        }
        foreach (XRTexture2D slice in _slices) Release(() => slice.Destroy(), ref failures);
        Release(() => _asset.Destroy(), ref failures);
        if (failures is not null) throw new AggregateException("Generated impostor resource cleanup failed.", failures);
    }

    private static void Release(Action release, ref List<Exception>? failures)
    {
        try { release(); }
        catch (Exception error) { (failures ??= []).Add(error); }
    }
}
