using MemoryPack;
using System.Threading;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class XRTexture2D
{
    [YamlIgnore]
    [MemoryPackIgnore]
    private int _awaitingFirstResidency;

    /// <summary>
    /// Gets whether the current mipmaps are only a loading placeholder and no streamed
    /// resident data is published yet. Renderers that can show a per-binding fallback use it
    /// instead of the placeholder image while this value is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// This state is runtime-only. It is not serialized and is not part of the asset file or
    /// of any cache key. The importer sets it on a placeholder that waits for streamed data.
    /// The first imported resident publication clears it, also when that publication carries
    /// the filler image of a missing source.
    /// </remarks>
    [YamlIgnore]
    [MemoryPackIgnore]
    internal bool IsAwaitingFirstResidency
        => Volatile.Read(ref _awaitingFirstResidency) != 0;

    /// <summary>
    /// Marks the current mipmaps as a loading placeholder until the first imported resident
    /// publication. Call this only on a texture that is registered for imported streaming;
    /// otherwise the state stays set and renderers keep their per-binding fallback.
    /// </summary>
    public void MarkAwaitingFirstResidency()
        => Volatile.Write(ref _awaitingFirstResidency, 1);

    /// <summary>Clears the placeholder state when real resident data becomes the texture source.</summary>
    private void ClearAwaitingFirstResidency()
        => Volatile.Write(ref _awaitingFirstResidency, 0);
}
