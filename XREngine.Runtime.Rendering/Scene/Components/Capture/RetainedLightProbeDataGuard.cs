using System.Threading;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Colors;
using XREngine.Rendering;

namespace XREngine.Components.Capture.Lights;

/// <summary>
/// Retains the immutable decoded probe closure. Supported image/mip changes invalidate it;
/// unsafe pixel writes must call Mipmap2D.Invalidate before subsequent publication.
/// </summary>
internal sealed class RetainedLightProbeDataGuard : IDisposable
{
    private readonly ImageState[] _images;
    private int _changed;
    private bool _disposed;

    internal RetainedLightProbeDataGuard(RetainedLightProbeIblProfile profile)
    {
        List<XRTexture2D> images = [];
        foreach (XRTexture2D image in new[] { profile.SourceIrradiance, profile.SourcePrefilter, profile.Irradiance, profile.Prefilter })
            if (!images.Any(existing => ReferenceEquals(existing, image))) images.Add(image);
        _images = new ImageState[images.Count];
        for (int index = 0; index < images.Count; index++)
        {
            XRTexture2D image = images[index];
            MipState[] mips = new MipState[image.Mipmaps.Length];
            for (int level = 0; level < mips.Length; level++)
            {
                Mipmap2D mip = image.Mipmaps[level];
                DataSource data = mip.Data!;
                mips[level] = new(mip, data, data.Address, data.Length);
            }
            _images[index] = new(image, image.Mipmaps, image.CanonicalSourceContentGeneration, image.Rectangle, mips);
        }
        try
        {
            foreach (ImageState state in _images)
            {
                XRTexture2D image = state.Image;
                image.PropertyChanging += SourceChanging;
                image.PushDataRequested += SourceInvalidated;
                image.GenerateMipmapsRequested += SourceInvalidated;
                image.ClearRequested += SourceCleared;
                image.PushMipLevelRequested += SourceMipPushed;
                foreach (MipState mip in state.States)
                {
                    mip.Mip.PropertyChanging += SourceChanging;
                    mip.Mip.Invalidated += SourceInvalidated;
                }
            }
            if (!IsCurrent) throw new InvalidDataException("WebGPU.RetainedProbe.SourceChanged: image state changed while the retained generation was being sealed.");
        }
        catch { Dispose(); throw; }
    }

    internal bool IsCurrent
    {
        get
        {
            if (_disposed || Volatile.Read(ref _changed) != 0) return false;
            for (int index = 0; index < _images.Length; index++)
            {
                ImageState image = _images[index];
                if (image.Image.IsDestroyed || image.Rectangle != image.Image.Rectangle || image.Generation != image.Image.CanonicalSourceContentGeneration ||
                    !ReferenceEquals(image.Mips, image.Image.Mipmaps) || image.Mips.Length != image.States.Length) return false;
                for (int level = 0; level < image.States.Length; level++)
                {
                    MipState mip = image.States[level];
                    if (!ReferenceEquals(image.Mips[level], mip.Mip) || !ReferenceEquals(mip.Mip.Data, mip.Data) ||
                        mip.Data.IsDisposed || mip.Data.Address != mip.Address || mip.Data.Length != mip.Length) return false;
                }
            }
            return Volatile.Read(ref _changed) == 0;
        }
    }

    private void SourceChanging(object? sender, IXRPropertyChangingEventArgs args)
    {
        if (sender is XRTexture2D && args.PropertyName is "Name" or "FilePath") return;
        SourceInvalidated();
    }

    private void SourceCleared(ColorF4 color, int level) => SourceInvalidated();
    private bool SourceMipPushed(int level) { SourceInvalidated(); return false; }

    private void SourceInvalidated() => Interlocked.Exchange(ref _changed, 1);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (ImageState image in _images)
        {
            image.Image.PropertyChanging -= SourceChanging;
            image.Image.PushDataRequested -= SourceInvalidated;
            image.Image.GenerateMipmapsRequested -= SourceInvalidated;
            image.Image.ClearRequested -= SourceCleared;
            image.Image.PushMipLevelRequested -= SourceMipPushed;
            foreach (MipState mip in image.States)
            {
                mip.Mip.PropertyChanging -= SourceChanging;
                mip.Mip.Invalidated -= SourceInvalidated;
            }
        }
    }

    private sealed record ImageState(XRTexture2D Image, Mipmap2D[] Mips, ulong Generation, bool Rectangle, MipState[] States);
    private readonly record struct MipState(Mipmap2D Mip, DataSource Data, VoidPtr Address, uint Length);
}
