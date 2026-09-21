using System.Threading;
using XREngine.Data.Core;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>Tracks authored texture changes without polling or reading GPU texels.</summary>
internal sealed class DDGIMaterialTextureEntry : IDisposable
{
    private Mipmap2D[] _observedMips = [];
    private long _revision = 1;

    public XRTexture2D Source { get; }
    public int Layer { get; }
    public ulong LastUsedFrame { get; set; }
    public long CopiedRevision { get; set; }
    public bool CopiedWhileStreaming { get; set; }
    public long PendingRevision { get; set; }
    public bool PendingWhileStreaming { get; set; }
    public long Revision => Volatile.Read(ref _revision);
    public bool IsGpuAuthored => Source.IsGpuWritable || Source.Mipmaps.Length == 0 || Source.Mipmaps[0].Data is null;
    public bool IsStreaming => Source.RuntimeManagedProgressiveUploadActive || Source.RuntimeManagedProgressiveFinalizePending;

    public DDGIMaterialTextureEntry(XRTexture2D source, int layer)
    {
        Source = source;
        Layer = layer;
        source.PropertyChanged += OnPropertyChanged;
        source.PushDataRequested += Invalidate;
        source.Resized += Invalidate;
        RefreshMipmapSubscriptions();
    }

    public void Invalidate() => Interlocked.Increment(ref _revision);

    private void OnPropertyChanged(object? sender, IXRPropertyChangedEventArgs args) => Invalidate();

    public void RefreshMipmapSubscriptions()
    {
        Mipmap2D[] mips = Source.Mipmaps;
        bool changed = mips.Length != _observedMips.Length;
        for (int i = 0; !changed && i < mips.Length; i++)
            changed |= !ReferenceEquals(mips[i], _observedMips[i]);
        if (!changed)
            return;
        UnsubscribeMips();
        // This snapshot is replaced only when the authored mip chain changes.
        _observedMips = (Mipmap2D[])mips.Clone();
        for (int i = 0; i < _observedMips.Length; i++)
        {
            _observedMips[i].PropertyChanged += OnPropertyChanged;
            _observedMips[i].Invalidated += Invalidate;
        }
        Invalidate();
    }

    private void UnsubscribeMips()
    {
        for (int i = 0; i < _observedMips.Length; i++)
        {
            _observedMips[i].PropertyChanged -= OnPropertyChanged;
            _observedMips[i].Invalidated -= Invalidate;
        }
    }

    public void Dispose()
    {
        Source.PropertyChanged -= OnPropertyChanged;
        Source.PushDataRequested -= Invalidate;
        Source.Resized -= Invalidate;
        UnsubscribeMips();
    }
}
