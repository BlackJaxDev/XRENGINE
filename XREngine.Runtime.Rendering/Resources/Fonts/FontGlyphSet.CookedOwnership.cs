using XREngine.Data;

namespace XREngine.Rendering;

public partial class FontGlyphSet
{
    // Imported fonts may share their atlas. Only the published codec transfers ownership.
    private XRTexture2D? _ownedCookedBitmapAtlas;
    private DataSource?[]? _ownedCookedBitmapMipSources;

    /// <summary>Transfers ownership of this decoder-created atlas and its native mip payloads to the font.</summary>
    internal void InstallOwnedCookedBitmapAtlas(XRTexture2D atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        if (!ReferenceEquals(Atlas, atlas) || _ownedCookedBitmapAtlas is not null)
            throw new InvalidOperationException("Cooked bitmap atlas ownership requires the font's unowned atlas.");

        Mipmap2D[] mips = atlas.Mipmaps;
        if (mips.Length == 0)
            throw new InvalidDataException("Cooked bitmap atlas has no mipmaps to own.");
        DataSource?[] sources = new DataSource[mips.Length];
        for (int level = 0; level < mips.Length; level++)
            sources[level] = mips[level].Data
                ?? throw new InvalidDataException($"Cooked bitmap atlas mip {level} has no native payload.");
        SetField(ref _ownedCookedBitmapMipSources, sources);
        SetField(ref _ownedCookedBitmapAtlas, atlas);
    }

    protected override void OnDestroying()
    {
        List<Exception>? failures = null;
        try { base.OnDestroying(); }
        catch (Exception error) { (failures ??= []).Add(error); }

        XRTexture2D? atlas = _ownedCookedBitmapAtlas;
        if (atlas is not null)
        {
            try
            {
                atlas.Destroy(now: true);
                if (!atlas.IsDestroyed)
                    throw new InvalidOperationException("Cooked bitmap atlas destruction was vetoed.");
            }
            catch (Exception error) { (failures ??= []).Add(error); }

            // A live API wrapper may still need its source mip data if retirement failed.
            if (atlas.IsDestroyed && _ownedCookedBitmapMipSources is { } sources)
            {
                bool released = true;
                for (int level = 0; level < sources.Length; level++)
                {
                    DataSource? source = sources[level];
                    if (source is null)
                        continue;
                    try
                    {
                        source.Dispose();
                        sources[level] = null;
                    }
                    catch (Exception error)
                    {
                        released = false;
                        (failures ??= []).Add(error);
                    }
                }
                if (released)
                {
                    SetField(ref _ownedCookedBitmapMipSources, null);
                    SetField(ref _ownedCookedBitmapAtlas, null);
                }
            }
        }
        if (failures is not null)
            throw new AggregateException("Cooked bitmap font resources could not be fully released.", failures);
    }
}
