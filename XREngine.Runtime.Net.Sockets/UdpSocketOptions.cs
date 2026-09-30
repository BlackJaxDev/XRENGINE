using System.Net.Sockets;

namespace XREngine.Networking;

public static class UdpSocketOptions
{
    private const int SioUdpConnectionReset = unchecked((int)0x9800000C);
    private static readonly byte[] DisableConnectionResetInput = [0, 0, 0, 0];

    public static void DisableConnectionReset(UdpClient client, string context)
    {
        if (!OperatingSystem.IsWindows())
            return;

        Socket socket = client.Client;
        if (socket.AddressFamily != AddressFamily.InterNetwork)
            return;

        try
        {
            socket.IOControl(SioUdpConnectionReset, DisableConnectionResetInput, null);
        }
        catch (ObjectDisposedException)
        {
            // Socket lifetime races during shutdown are harmless here.
        }
        catch (SocketException ex)
        {
            Debug.NetworkingWarning("[Net] Failed to disable UDP connection reset handling for {0}: {1}", context, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            Debug.NetworkingWarning("[Net] UDP connection reset handling is not configurable for {0}: {1}", context, ex.Message);
        }
    }
}
