namespace XREngine.Rendering;

/// <summary>
/// Installation point for the host asset resolver used by render-asset serializers.
/// </summary>
public static class RenderAssetSerializationServices
{
    private static IRenderAssetSerializationServices? _current;

    public static IRenderAssetSerializationServices Current
        => Volatile.Read(ref _current)
            ?? throw new InvalidOperationException(
                "Rendering asset serialization services are not installed. " +
                "Install an IRenderAssetSerializationServices implementation before loading YAML render assets.");

    public static void Install(IRenderAssetSerializationServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Volatile.Write(ref _current, services);
    }

    public static void Uninstall(IRenderAssetSerializationServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Interlocked.CompareExchange(ref _current, null, services);
    }
}
