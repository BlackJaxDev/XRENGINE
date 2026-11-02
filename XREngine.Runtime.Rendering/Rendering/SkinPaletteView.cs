namespace XREngine.Rendering;

/// <summary>
/// A shared pair of current and previous skin palette buffers. A palette producer, such as the
/// GPU physics-chain dispatcher, binds many renderers to one view, each renderer at its own
/// slice. The producer then publishes the buffers of each new output once, and every bound
/// renderer reads them through the view.
/// </summary>
public sealed class SkinPaletteView
{
    private XRDataBuffer? _current;
    private XRDataBuffer? _previous;
    private ulong _publicationVersion;

    /// <summary>The buffer that holds the current palettes.</summary>
    public XRDataBuffer? Current => Volatile.Read(ref _current);

    /// <summary>The buffer that holds the palettes of the prior output.</summary>
    public XRDataBuffer? Previous => Volatile.Read(ref _previous);

    /// <summary>The number of publications. Each publication advances it by one.</summary>
    public ulong PublicationVersion => Volatile.Read(ref _publicationVersion);

    /// <summary>Publishes the buffers of a new output to every bound renderer.</summary>
    internal void Publish(XRDataBuffer? current, XRDataBuffer? previous)
    {
        Volatile.Write(ref _previous, previous);
        Volatile.Write(ref _current, current);
        Interlocked.Increment(ref _publicationVersion);
    }
}
