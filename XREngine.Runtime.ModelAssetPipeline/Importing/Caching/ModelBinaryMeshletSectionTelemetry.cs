namespace XREngine.Rendering.Models.Caching;

internal static class ModelBinaryMeshletSectionTelemetry
{
    private static long _primary;
    private static long _secondary;
    private static long _repaired;
    private static long _unmatched;

    public static long Primary => Interlocked.Read(ref _primary);
    public static long Secondary => Interlocked.Read(ref _secondary);
    public static long Repaired => Interlocked.Read(ref _repaired);
    public static long Unmatched => Interlocked.Read(ref _unmatched);

    public static void Record(int primary, int secondary, int repaired, int unmatched)
    {
        Interlocked.Add(ref _primary, primary);
        Interlocked.Add(ref _secondary, secondary);
        Interlocked.Add(ref _repaired, repaired);
        Interlocked.Add(ref _unmatched, unmatched);
    }
}
