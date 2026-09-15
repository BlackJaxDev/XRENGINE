using System.Buffers.Binary;
using System.Net.Security;

namespace XREngine.Networking;

/// <summary>Bounded datagram framing inside the platform TLS implementation. Never used on plaintext streams.</summary>
internal static class RealtimeTlsFraming
{
    internal const int MaximumDatagramBytes = 65_507;
    internal static readonly SslApplicationProtocol Protocol = new("xrengine-realtime/1");

    internal static async ValueTask<int> ReadAsync(SslStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await stream.ReadExactlyAsync(buffer[..4], cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(buffer.Span);
        if (length is 0 or > MaximumDatagramBytes)
            throw new InvalidDataException("Invalid encrypted realtime frame length.");
        await stream.ReadExactlyAsync(buffer[..(int)length], cancellationToken).ConfigureAwait(false);
        return (int)length;
    }

    internal static async ValueTask WriteAsync(SslStream stream, ReadOnlyMemory<byte> datagram, Memory<byte> frame, CancellationToken cancellationToken)
    {
        if (datagram.Length is 0 or > MaximumDatagramBytes)
            throw new InvalidDataException("Invalid encrypted realtime datagram length.");
        BinaryPrimitives.WriteUInt32BigEndian(frame.Span, (uint)datagram.Length);
        datagram.CopyTo(frame[4..]);
        await stream.WriteAsync(frame[..(datagram.Length + 4)], cancellationToken).ConfigureAwait(false);
    }
}
