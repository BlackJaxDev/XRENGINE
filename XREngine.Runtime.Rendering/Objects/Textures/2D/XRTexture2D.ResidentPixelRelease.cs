using System.Threading;

namespace XREngine.Rendering;

public partial class XRTexture2D
{
    // The mip array most recently applied from streaming resident data. Streaming
    // decodes (and reuse-cache clones) build these mips fresh, so this texture owns
    // their pixel sources exclusively and may free them.
    private Mipmap2D[]? _streamingResidentMipmaps;
    private int _residentPixelsReleased;

    /// <summary>
    /// True when the CPU pixels of the streaming-applied mip chain were released
    /// after their GPU upload was published. Each mip keeps its width, height and
    /// formats; the pixels reload from the texture's streaming source when a
    /// consumer needs them (renderer restart rehydration, serialization).
    /// </summary>
    internal bool ResidentPixelsReleased
        => Volatile.Read(ref _residentPixelsReleased) != 0 &&
           ReferenceEquals(Mipmaps, Volatile.Read(ref _streamingResidentMipmaps));

    /// <summary>
    /// Records <paramref name="mipmaps"/> as the streaming-owned mip chain that was
    /// just assigned to <see cref="Mipmaps"/>. Called under the imported-source
    /// metadata lock by the resident-data apply paths.
    /// </summary>
    private void TrackStreamingResidentMipmaps(Mipmap2D[] mipmaps)
    {
        Volatile.Write(ref _streamingResidentMipmaps, mipmaps);
        Volatile.Write(ref _residentPixelsReleased, 0);
    }

    /// <summary>
    /// Ensures the mips hold CPU pixels, reloading a released streaming chain from
    /// its streaming source synchronously. For cold consumers such as serialization
    /// and export; never call it on the render thread.
    /// </summary>
    public bool TryRestoreReleasedResidentPixels(out string? failureReason)
    {
        failureReason = null;
        return !ResidentPixelsReleased ||
               ImportedTextureStreamingManager.Instance.TryReloadReleasedResidentPixels(this, out failureReason);
    }

    /// <summary>
    /// Puts reloaded pixels back into the released chain <paramref name="published"/>,
    /// mip by mip, if it is still this texture's streaming chain.
    /// </summary>
    internal bool RestoreStreamingResidentPixels(Mipmap2D[] published, Mipmap2D[] reloaded)
    {
        lock (_importedSourceMetadataWriteSync)
        {
            if (!ReferenceEquals(Mipmaps, published) ||
                !ReferenceEquals(published, Volatile.Read(ref _streamingResidentMipmaps)) ||
                Volatile.Read(ref _residentPixelsReleased) == 0)
            {
                return false;
            }

            for (int mipIndex = 0; mipIndex < published.Length; mipIndex++)
            {
                Mipmap2D target = published[mipIndex];
                Mipmap2D source = reloaded[mipIndex];
                if (target.Width != source.Width || target.Height != source.Height)
                    return false;
            }

            // The reloaded mips are discarded; their sources move to the published mips.
            for (int mipIndex = 0; mipIndex < published.Length; mipIndex++)
                published[mipIndex].Data = reloaded[mipIndex].Data;

            Volatile.Write(ref _residentPixelsReleased, 0);
            return true;
        }
    }

    /// <summary>
    /// Frees the CPU pixels of the published streaming mip chain without touching
    /// <see cref="Mipmaps"/> itself, so no storage-property change recreates the
    /// GPU image. Only the chain applied from streaming resident data is released;
    /// authored or shared mips are never freed here.
    /// </summary>
    /// <returns>The number of pixel bytes released.</returns>
    internal long ReleaseStreamingResidentPixels()
    {
        lock (_importedSourceMetadataWriteSync)
        {
            Mipmap2D[]? mipmaps = Mipmaps;
            if (mipmaps is not { Length: > 0 } ||
                !ReferenceEquals(mipmaps, Volatile.Read(ref _streamingResidentMipmaps)) ||
                Volatile.Read(ref _residentPixelsReleased) != 0)
            {
                return 0L;
            }

            long releasedBytes = 0L;
            for (int mipIndex = 0; mipIndex < mipmaps.Length; mipIndex++)
            {
                Mipmap2D? mipmap = mipmaps[mipIndex];
                if (mipmap?.Data is not { } data)
                    continue;

                releasedBytes += data.Length;
                // The Data setter disposes the previous source.
                mipmap.Data = null;
            }

            Volatile.Write(ref _residentPixelsReleased, 1);
            return releasedBytes;
        }
    }
}
