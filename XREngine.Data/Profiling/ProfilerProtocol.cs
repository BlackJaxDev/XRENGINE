namespace XREngine.Data.Profiling;

/// <summary>
/// Shared constants and enums for the UDP profiler wire protocol.
/// Used by both the sender (engine-side) and receiver (profiler app).
/// </summary>
public static class ProfilerProtocol
{
    /// <summary>Default UDP port for profiler traffic.</summary>
    public const int DefaultPort = 9142;

    /// <summary>
    /// Maximum datagram payload size (bytes).
    /// Below the 65,507-byte UDP limit to leave room for headers.
    /// </summary>
    public const int MaxDatagramSize = 65_000;

    /// <summary>
    /// Environment variable that, when set to "1", enables the profiler sender.
    /// </summary>
    public const string EnabledEnvVar = "XRE_PROFILER_ENABLED";

    /// <summary>
    /// Environment variable that overrides the default port.
    /// </summary>
    public const string PortEnvVar = "XRE_PROFILER_PORT";

    /// <summary>
    /// Wire-level message type IDs. First byte of every datagram.
    /// </summary>
    public enum MessageType : byte
    {
        ProfilerFrame      = 0x01,
        RenderStats        = 0x02,
        ThreadAllocations  = 0x03,
        BvhMetrics         = 0x04,
        JobSystemStats     = 0x05,
        MainThreadInvokes  = 0x06,
        Heartbeat          = 0x07,
    }

    /// <summary>
    /// Header size: 1 byte message type + 4 bytes payload length.
    /// </summary>
    public const int HeaderSize = 5;

    /// <summary>
    /// Maximum payload size after subtracting the header.
    /// </summary>
    public const int MaxPayloadSize = MaxDatagramSize - HeaderSize;

    /// <summary>
    /// Target send rate for the sender background thread.
    /// </summary>
    public const int SendIntervalMs = 33; // ~30 Hz

    /// <summary>
    /// Heartbeat interval (ms).
    /// </summary>
    public const int HeartbeatIntervalMs = 1000;

    /// <summary>
    /// Writes a framed message into the destination buffer.
    /// Returns total bytes written (header + payload), or -1 if the payload is too large.
    /// </summary>
    public static int WriteFrame(byte[] destination, MessageType type, ReadOnlySpan<byte> payload)
    {
        int total = HeaderSize + payload.Length;
        if (total > destination.Length || payload.Length > MaxPayloadSize)
            return -1;

        destination[0] = (byte)type;
        BitConverter.TryWriteBytes(destination.AsSpan(1, 4), payload.Length);
        payload.CopyTo(destination.AsSpan(HeaderSize));
        return total;
    }

    /// <summary>
    /// Reads message type and payload from a received datagram.
    /// Returns false if the datagram is malformed.
    /// </summary>
    public static bool TryReadFrame(ReadOnlySpan<byte> datagram, out MessageType type, out ReadOnlySpan<byte> payload)
    {
        type = default;
        payload = default;

        if (datagram.Length < HeaderSize)
            return false;

        type = (MessageType)datagram[0];
        int length = BitConverter.ToInt32(datagram.Slice(1, 4));

        if (length < 0 || HeaderSize + length > datagram.Length)
            return false;

        payload = datagram.Slice(HeaderSize, length);
        return true;
    }
}
