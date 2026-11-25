using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.UI;

/// <summary>Shares bounded, privately owned pixel and sampler copies between captured draws.</summary>
internal sealed class UITextureSnapshot
{
    private const long MaximumBytes = 128L * 1024 * 1024;
    private const long MaximumTextureBytes = 64L * 1024 * 1024;
    private const int MaximumSnapshots = 256;
    private static readonly object Gate = new();
    private static readonly Dictionary<XRTexture2D, UITextureSnapshot> Cache = new(ReferenceEqualityComparer.Instance);
    private static readonly List<UITextureSnapshot> Quarantined = [];
    private static long _bytes;
    private static int _count;
    private int _references = 1;
    private readonly XRTexture2D _source;
    private readonly Mipmap2D[] _sourceMips;
    private readonly long[] _mipRevisions;
    private readonly long _sourceRevision;
    private readonly ulong _contentGeneration;
    private readonly long _reservedBytes;
    private readonly Action _dispatchSourceChanged;
    private int _sourceChangeQueued;
    internal event Action? SourceChanged;

    private UITextureSnapshot(XRTexture2D source, XRTexture2D texture, long bytes)
    {
        _source = source;
        _dispatchSourceChanged = DispatchSourceChanged;
        Texture = texture;
        _reservedBytes = bytes;
        _sourceRevision = source.SourceDataRevision;
        _contentGeneration = source.CanonicalSourceContentGeneration;
        _sourceMips = (Mipmap2D[])source.Mipmaps.Clone();
        _mipRevisions = new long[_sourceMips.Length];
        _source.PropertyChanging += SourcePropertyChanging;
        _source.PushDataRequested += NotifySourceChanged;
        for (int index = 0; index < _sourceMips.Length; index++)
        {
            _mipRevisions[index] = _sourceMips[index].DataRevision;
            _sourceMips[index].PropertyChanging += SourcePropertyChanging;
            _sourceMips[index].Invalidated += NotifySourceChanged;
        }
    }

    internal XRTexture2D Texture { get; }

    internal static bool TryAcquire(XRTexture source, out UITextureSnapshot? snapshot, out string? failure)
    {
        snapshot = null;
        failure = null;
        if (source is not XRTexture2D texture || texture.GetType() != typeof(XRTexture2D) ||
            texture.FrameBufferAttachment is not null || texture.GrabPass is not null || texture.MultiSample ||
            texture.Rectangle || texture.UsesOpenGlExternalMemoryImport || texture.ShouldLoadDataFromInternalPBO ||
            texture.CanonicalPublicationLifetime is not null)
        {
            failure = "UI.TextureCapture.ProducerAdapterRequired: GPU outputs, custom texture types, and non-2D textures require a producer generation lease.";
            return false;
        }
        lock (Gate)
        {
            if ((texture.ImportedSourceMetadataEpoch & 1UL) != 0)
            {
                failure = "UI.TextureCapture.MetadataPending: the source image is being updated.";
                return false;
            }
            if (Cache.TryGetValue(texture, out UITextureSnapshot? cached) && cached.Matches(texture))
            {
                cached.Retain();
                snapshot = cached;
                return true;
            }
            Mipmap2D[] mips = texture.Mipmaps;
            if (mips.Length is 0 or > 32)
            {
                failure = "UI.TextureCapture.MipsPending: a bounded, complete CPU mip chain is required.";
                return false;
            }
            long bytes = 4096;
            for (int index = 0; index < mips.Length; index++)
            {
                if (mips[index] is not { Width: > 0, Height: > 0, Data: { } data } || data.Length == 0)
                {
                    failure = "UI.TextureCapture.PixelsPending: retain a CPU image or provide a producer generation lease.";
                    return false;
                }
                bytes = checked(bytes + data.Length + 256L);
            }
            if (bytes > MaximumTextureBytes)
            {
                failure = "UI.TextureCapture.Capacity: the immutable texture exceeds the capture limit.";
                return false;
            }
            if (Cache.Count >= MaximumSnapshots || _bytes + bytes > MaximumBytes || _count >= MaximumSnapshots)
                EvictUnused();
            if (_bytes + bytes > MaximumBytes || _count >= MaximumSnapshots)
            {
                failure = "UI.TextureCapture.Capacity: retained texture generations exhaust the capture budget.";
                return false;
            }
            _bytes += bytes;
            _count++;
            XRTexture2D? copy = null;
            Mipmap2D[] ownedMips = new Mipmap2D[mips.Length];
            try
            {
                long sourceRevision = texture.SourceDataRevision;
                ulong metadataEpoch = texture.ImportedSourceMetadataEpoch;
                ulong contentGeneration = texture.CanonicalSourceContentGeneration;
                long[] revisions = new long[mips.Length];
                using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
                for (int index = 0; index < mips.Length; index++)
                {
                    revisions[index] = mips[index].DataRevision;
                    ownedMips[index] = mips[index].Clone(cloneImage: true);
                }
                copy = new XRTexture2D(1, 1, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte)
                {
                    Mipmaps = ownedMips,
                    SamplerName = texture.SamplerName,
                    AutoGenerateMipmaps = texture.AutoGenerateMipmaps,
                    MinLOD = texture.MinLOD, MaxLOD = texture.MaxLOD,
                    LargestMipmapLevel = texture.LargestMipmapLevel,
                    SmallestAllowedMipmapLevel = texture.SmallestAllowedMipmapLevel,
                    Resizable = texture.Resizable, SizedInternalFormat = texture.SizedInternalFormat,
                    MagFilter = texture.MagFilter, MinFilter = texture.MinFilter,
                    UWrap = texture.UWrap, VWrap = texture.VWrap,
                    LodBias = texture.LodBias, MaxAnisotropy = texture.MaxAnisotropy,
                    EnableComparison = texture.EnableComparison, CompareFunc = texture.CompareFunc,
                    InternalCompression = texture.InternalCompression, RequiresStorageUsage = texture.RequiresStorageUsage,
                    ImportedColorSpace = texture.ImportedColorSpace, ImportedUsage = texture.ImportedUsage,
                    ImportedNormalMapFlipGreen = texture.ImportedNormalMapFlipGreen,
                    AlphaAsTransparency = texture.AlphaAsTransparency,
                    FixedSampleLocations = texture.FixedSampleLocations, ExclusiveSharing = texture.ExclusiveSharing,
                    AllowAutomaticImportedTextureStreaming = false,
                };
                if (!ReferenceEquals(mips, texture.Mipmaps) || sourceRevision != texture.SourceDataRevision ||
                    contentGeneration != texture.CanonicalSourceContentGeneration || metadataEpoch != texture.ImportedSourceMetadataEpoch)
                    throw new InvalidOperationException("The texture changed during immutable capture.");
                for (int index = 0; index < mips.Length; index++)
                    if (revisions[index] != mips[index].DataRevision)
                        throw new InvalidOperationException("A texture mip changed during immutable capture.");
                UITextureSnapshot replacement = new(texture, copy, bytes);
                publication.Complete();
                Cache[texture] = replacement;
                replacement.Retain();
                snapshot = replacement;
                cached?.Release();
                return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException)
            {
                foreach (Mipmap2D? mip in ownedMips)
                    mip?.Data?.Dispose();
                copy?.Destroy();
                _bytes -= bytes;
                _count--;
                failure = $"UI.TextureCapture.Failed: {exception.Message}";
                return false;
            }
        }
    }

    internal bool Matches(XRTexture source)
    {
        if (!ReferenceEquals(_source, source) || source is not XRTexture2D texture ||
            (texture.ImportedSourceMetadataEpoch & 1UL) != 0 || texture.SourceDataRevision != _sourceRevision || texture.CanonicalSourceContentGeneration != _contentGeneration ||
            texture.Mipmaps.Length != _sourceMips.Length || texture.FrameBufferAttachment is not null ||
            texture.GrabPass is not null || texture.MultiSample || texture.Rectangle || texture.UsesOpenGlExternalMemoryImport ||
            texture.ShouldLoadDataFromInternalPBO || texture.CanonicalPublicationLifetime is not null || !SamplerEquals(texture, Texture))
            return false;
        for (int index = 0; index < _sourceMips.Length; index++)
            if (!ReferenceEquals(texture.Mipmaps[index], _sourceMips[index]) ||
                _sourceMips[index].DataRevision != _mipRevisions[index])
                return false;
        return true;
    }

    private static bool SamplerEquals(XRTexture2D a, XRTexture2D b)
        => a.SamplerName == b.SamplerName && a.AutoGenerateMipmaps == b.AutoGenerateMipmaps &&
           a.MinLOD == b.MinLOD && a.MaxLOD == b.MaxLOD && a.LargestMipmapLevel == b.LargestMipmapLevel &&
           a.SmallestAllowedMipmapLevel == b.SmallestAllowedMipmapLevel && a.Resizable == b.Resizable &&
           a.SizedInternalFormat == b.SizedInternalFormat && a.MagFilter == b.MagFilter && a.MinFilter == b.MinFilter &&
           a.UWrap == b.UWrap && a.VWrap == b.VWrap && a.LodBias.Equals(b.LodBias) && a.MaxAnisotropy.Equals(b.MaxAnisotropy) &&
           a.EnableComparison == b.EnableComparison && a.CompareFunc == b.CompareFunc &&
           a.InternalCompression == b.InternalCompression && a.RequiresStorageUsage == b.RequiresStorageUsage &&
           a.ImportedColorSpace == b.ImportedColorSpace && a.ImportedUsage == b.ImportedUsage &&
           a.ImportedNormalMapFlipGreen == b.ImportedNormalMapFlipGreen && a.AlphaAsTransparency == b.AlphaAsTransparency &&
           a.FixedSampleLocations == b.FixedSampleLocations && a.ExclusiveSharing == b.ExclusiveSharing;

    private static void EvictUnused()
    {
        XRTexture2D? key = null;
        UITextureSnapshot? victim = null;
        foreach (var entry in Cache)
            if (Volatile.Read(ref entry.Value._references) == 1)
            {
                key = entry.Key;
                victim = entry.Value;
                break;
            }
        if (key is not null)
        {
            Cache.Remove(key);
            victim!.Release();
        }
    }

    internal void Retain()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _references);
            ObjectDisposedException.ThrowIf(current == 0, this);
        } while (Interlocked.CompareExchange(ref _references, checked(current + 1), current) != current);
    }

    internal void Release()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _references);
            if (current == 0)
                throw new InvalidOperationException("UI texture snapshot claim was released twice.");
        } while (Interlocked.CompareExchange(ref _references, current - 1, current) != current);
        if (current != 1)
            return;
        try
        {
            if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                ReleaseResources();
            else
                RuntimeEngine.EnqueueRenderThreadTask(ReleaseResources, "UI.TextureCapture.Release", RenderThreadJobKind.RenderPipelineResource);
        }
        catch (Exception exception) { Quarantine(exception); }
    }

    private void ReleaseResources()
    {
        try
        {
            UIDrawPayload.DestroyOwner(Texture);
            _source.PropertyChanging -= SourcePropertyChanging;
            _source.PushDataRequested -= NotifySourceChanged;
            foreach (Mipmap2D mip in _sourceMips)
            {
                mip.PropertyChanging -= SourcePropertyChanging;
                mip.Invalidated -= NotifySourceChanged;
            }
            foreach (Mipmap2D mip in Texture.Mipmaps)
                mip.Data?.Dispose();
            lock (Gate)
            {
                _bytes -= _reservedBytes;
                _count--;
            }
        }
        catch (Exception exception) { Quarantine(exception); }
    }

    private void SourcePropertyChanging(object? sender, IXRPropertyChangingEventArgs args)
        => NotifySourceChanged();

    private void NotifySourceChanged()
    {
        if (SourceChanged is null)
            return;
        if (RuntimeRenderingHostServices.FrameTiming.IsAppThread)
        {
            SourceChanged?.Invoke();
            return;
        }
        if (Interlocked.Exchange(ref _sourceChangeQueued, 1) == 0)
            RuntimeEngine.EnqueueAppThreadTask(_dispatchSourceChanged, "UI.TextureCapture.SourceChanged");
    }

    private void DispatchSourceChanged()
    {
        Volatile.Write(ref _sourceChangeQueued, 0);
        SourceChanged?.Invoke();
    }

    private void Quarantine(Exception exception)
    {
        lock (Gate)
            Quarantined.Add(this);
        Debug.LogWarning($"UI.TextureCapture.RetirementFailed: resources and capture budget remain retained. {exception.Message}");
    }
}
