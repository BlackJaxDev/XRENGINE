using System.Net.Security;
using System.Security.Cryptography;
using System.Text;

namespace XREngine.Networking;

/// <summary>Accepts one bounded, credential-free HTTP upgrade; no HTTP proxying or alternate routes are exposed.</summary>
internal static class RealtimeWebSocketUpgrade
{
    internal static async Task AcceptAsync(SslStream stream, string expectedAuthority, HashSet<string> allowedOrigins,
        CancellationToken cancellationToken)
    {
        // Read exactly through the header terminator, preserving every following byte for the WebSocket parser.
        byte[] bytes = new byte[8192];
        int count = 0;
        while (count < bytes.Length)
        {
            await stream.ReadExactlyAsync(bytes.AsMemory(count, 1), cancellationToken).ConfigureAwait(false);
            byte value = bytes[count++];
            if (value is not (9 or 10 or 13) && (value < 32 || value > 126))
                throw new InvalidDataException("Invalid realtime upgrade header encoding.");
            if (count >= 4 && bytes.AsSpan(count - 4, 4).SequenceEqual("\r\n\r\n"u8))
                break;
        }
        if (count == bytes.Length)
            throw new InvalidDataException("Realtime upgrade headers exceed the limit.");
        string[] lines = Encoding.ASCII.GetString(bytes, 0, count).Split("\r\n", StringSplitOptions.None);
        if (lines[0] != "GET " + RealtimeWebSocketProtocol.Path + " HTTP/1.1")
            throw new InvalidDataException("Unsupported realtime upgrade route.");
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < lines.Length - 2; index++)
        {
            string line = lines[index];
            int separator = line.IndexOf(':');
            if (separator < 1 || char.IsWhiteSpace(line[0]) || line.IndexOfAny(['\r', '\n']) >= 0
                || line[..separator].Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                throw new InvalidDataException("Invalid realtime upgrade header.");
            if (!headers.TryAdd(line[..separator], line[(separator + 1)..].Trim()))
                throw new InvalidDataException("Repeated realtime upgrade headers are not allowed.");
        }
        if (!headers.TryGetValue("Host", out string? authority) || !string.Equals(authority, expectedAuthority, StringComparison.OrdinalIgnoreCase)
            || !headers.TryGetValue("Origin", out string? origin) || !allowedOrigins.Contains(origin)
            || !headers.TryGetValue("Upgrade", out string? upgrade) || !string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase)
            || !headers.TryGetValue("Connection", out string? connection) || !ContainsToken(connection, "Upgrade")
            || !headers.TryGetValue("Sec-WebSocket-Version", out string? version) || version != "13"
            || !headers.TryGetValue("Sec-WebSocket-Protocol", out string? protocols) || !ContainsToken(protocols, RealtimeWebSocketProtocol.Subprotocol, StringComparison.Ordinal)
            || !headers.TryGetValue("Sec-WebSocket-Key", out string? key)
            || headers.ContainsKey("Cookie") || headers.ContainsKey("Authorization") || headers.ContainsKey("Proxy-Authorization")
            || headers.ContainsKey("Content-Length") || headers.ContainsKey("Transfer-Encoding"))
            throw new InvalidDataException("Realtime upgrade origin, protocol, or credential policy rejected the request.");
        Span<byte> nonce = stackalloc byte[16];
        if (!Convert.TryFromBase64String(key, nonce, out int written) || written != 16)
            throw new InvalidDataException("Invalid realtime upgrade nonce.");
        // SHA-1 is mandated by the WebSocket handshake and is not used for authentication.
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "
            + accept + "\r\nSec-WebSocket-Protocol: " + RealtimeWebSocketProtocol.Subprotocol + "\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static bool ContainsToken(string header, string token, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        => header.Split(',').Any(value => string.Equals(value.Trim(), token, comparison));
}
