namespace XREngine.Networking;

/// <summary>Reports a transport failure without exposing a platform socket API in the kernel.</summary>
public sealed class NetworkTransportException(string message, int errorCode, Exception? innerException = null)
    : IOException(message, innerException)
{
    public int ErrorCode { get; } = errorCode;
}
