using System.Net;
using XREngine.Networking;

namespace XREngine;

public partial class ClientNetworkingManager
{
    private CancellationTokenSource? _webSocketStart;
    private readonly object _webSocketLifecycleLock = new();
    private bool _webSocketStarted;
    private string? _webSocketFailure;

    protected override bool UseBoundedRealtimeQueues => Transport == RealtimeTransportKind.WebSocket;

    protected override void OnRealtimeQueueOverflow()
    {
        _webSocketFailure = "Realtime send backlog exceeded; fresh admission and state resynchronization are required.";
        UdpSender?.Close();
        throw new NetworkTransportException(_webSocketFailure, 0);
    }

    /// <summary>Transport-only failure; successful connection does not imply successful player admission.</summary>
    public string? WebSocketTransportFailure
        => _webSocketFailure ?? (UdpSender as IRealtimeWebSocketTransport)?.Failure;

    /// <summary>
    /// Connects without blocking the browser thread, then runs the existing managed admission and replication protocol.
    /// The endpoint comes from a trusted handoff. The peer is only the protocol's routing identity, never a local socket.
    /// This manager is single-use; reconnect with a new manager and fresh control-plane admission to force a baseline resync.
    /// </summary>
    public async Task StartWebSocketAsync(Uri endpoint, IPEndPoint protocolPeer, CancellationToken cancellationToken = default)
    {
        if (_webSocketStarted || UdpSender is not null || _tlsTunnel is not null)
            throw new InvalidOperationException("Realtime manager has already started; reconnect requires a new manager and fresh admission.");
        if (Transport != RealtimeTransportKind.WebSocket || !IsManagedTransportRequested)
            throw new InvalidOperationException("Realtime WebSocket requires explicit transport selection and managed player admission.");
        if (!TryCreateManagedIdentity(out _, out string? error))
            throw new InvalidOperationException(error);
        RealtimeWebSocketProtocol.ValidateEndpoint(endpoint);
        if (NetworkTransportServices.Required is not IRealtimeWebSocketBackend backend)
            throw new NotSupportedException("The installed network backend does not support realtime WebSocket connections.");
        CancellationTokenSource start;
        lock (_webSocketLifecycleLock)
        {
            if (_webSocketStarted)
                throw new InvalidOperationException("Realtime manager has already started; reconnect requires a new manager and fresh admission.");
            _webSocketStarted = true;
            start = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _webSocketStart = start;
        }
        IRealtimeWebSocketTransport? connection = null;
        try
        {
            connection = await backend.ConnectWebSocketAsync(endpoint, protocolPeer, start.Token);
            start.Token.ThrowIfCancellationRequested();
            UdpSender = connection;
            UdpReceiver = connection;
            ServerIP = new IPEndPoint(protocolPeer.Address, protocolPeer.Port);
            LastServerError = null;
            PauseUntilReplicationAssignment();
            EnsureClientTick();
            StartManagedTransportHandshake();
        }
        catch
        {
            connection?.Dispose();
            UdpSender = UdpReceiver = null;
            _webSocketFailure = "Realtime WebSocket connection failed. " + RealtimeWireProtocol.UpdateRequiredMessage;
            throw;
        }
        finally
        {
            lock (_webSocketLifecycleLock)
            {
                _webSocketStart = null;
                start.Dispose();
            }
        }
    }

    /// <summary>Closes a suspended browser session immediately, clearing keys and stale replication state.</summary>
    public void SuspendWebSocket()
    {
        if (!_webSocketStarted)
            return;
        _webSocketFailure ??= "Realtime WebSocket suspended; resume requires fresh admission and a complete replication baseline.";
        CancelWebSocketStart();
        UdpSender?.Dispose();
        UdpSender = UdpReceiver = null;
        DisposeManagedTransport();
        HandleManagedTerminalCleanup(_webSocketFailure);
    }

    private void ObserveWebSocketFailure()
    {
        if (UdpSender is not IRealtimeWebSocketTransport { Connected: false } connection)
            return;
        _webSocketFailure = connection.Failure ?? "Realtime WebSocket closed; fresh admission is required.";
        SuspendWebSocket();
    }

    private void CancelWebSocketStart()
    {
        lock (_webSocketLifecycleLock)
            _webSocketStart?.Cancel();
        // The asynchronous connection attempt owns its continuation; never wait on the browser thread.
    }
}
