namespace XREngine.Rendering.Vulkan;

/// <summary>Last diagnostic texture readback's exact image identities; updated only on explicit capture.</summary>
public static class VulkanTextureReadbackDiagnostics
{
    private static string _lastResolution = string.Empty;
    private static string _lastMappedRead = string.Empty;
    private static string _lastTransition = string.Empty;
    private static int _copyGuardEnabled;

    /// <summary>Enables a GPU-written staging guard for an explicit readback investigation.</summary>
    public static void SetCopyGuardEnabled(bool enabled)
        => Volatile.Write(ref _copyGuardEnabled, enabled ? 1 : 0);

    internal static bool CopyGuardEnabled => Volatile.Read(ref _copyGuardEnabled) != 0;

    public static string GetLastResolution() => Volatile.Read(ref _lastResolution);

    public static string GetLastMappedRead() => Volatile.Read(ref _lastMappedRead);

    public static string GetLastTransition() => Volatile.Read(ref _lastTransition);

    internal static void Publish(string resolution) => Volatile.Write(ref _lastResolution, resolution);

    internal static void PublishTransition(string transition) => Volatile.Write(ref _lastTransition, transition);

    internal static void PublishMappedRead(in VulkanFrameDataSlice slice, ReadOnlySpan<byte> bytes, bool guarded, ulong requestedImage, ulong copiedImage, string transition)
        => Volatile.Write(ref _lastMappedRead,
            $"requestedImage={requestedImage} copiedImage={copiedImage} buffer={slice.Buffer.Handle} offset={slice.Offset} length={slice.Length} head={Convert.ToHexString(bytes[..Math.Min(bytes.Length, 32)])} guard={(guarded ? Convert.ToHexString(bytes[^4..]) : "disabled")} transition=[{transition}]");
}
