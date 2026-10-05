using XREngine.Networking;

namespace XREngine.Browser;

internal sealed partial class BrowserEngineSession
{
    private CancellationTokenSource? _networkConnect;
    private ClientNetworkingManager? _networkClient;
    private string _networkState = "local";

    public string NetworkState
    {
        get
        {
            if (_networkConnect is not null && _networkState == "connecting")
                return "connecting";
            if (_networkClient is not { } client)
                return _networkState;
            if (!ReferenceEquals(Engine.Networking, client)
                || client.WebSocketTransportFailure is not null || client.ManagedTransportFailure is not null)
                return "failed";
            if (client.IsGameplayReady && client.UdpSender?.Connected == true)
                return "ready";
            return client.HasValidLocalAssignment ? "synchronizing" : "authenticating";
        }
    }

    /// <summary>Samples non-secret client acknowledgement and matching local pawn pose on demand.</summary>
    public string GetNetworkSimulationStatus()
    {
        if (_disposed || !_running || !_engineInitialized || _networkClient is not { } client
            || !ReferenceEquals(Engine.Networking, client))
            return "network=inactive";

        string state = NetworkState;
        if (state != "ready")
            return $"network={state}";

        bool ownsAssignment = _localPlayer?.PlayerInfo is { } player
            && player.SessionId == client.AssignedSessionId
            && player.ServerIndex == client.PrimaryAssignedServerPlayerIndex
            && player.NetworkEntityId == client.PrimaryAssignedEntityId;
        XREngine.Scene.Transforms.TransformBase? transform = ownsAssignment
            ? _localPlayer?.ControlledPawnComponent?.SceneNode?.Transform : null;
        if (transform is null)
            return FormattableString.Invariant($"network=ready; player={client.PrimaryAssignedServerPlayerIndex}; clientAcknowledged={client.LastProcessedInputSequence}; pose=unavailable");

        System.Numerics.Vector3 position = transform.WorldTranslation;
        return FormattableString.Invariant($"network=ready; player={client.PrimaryAssignedServerPlayerIndex}; clientAcknowledged={client.LastProcessedInputSequence}; pose=({position.X:R},{position.Y:R},{position.Z:R})");
    }

    /// <summary>Joins only the loaded verified world; the caller obtains fresh admission from its trusted control plane.</summary>
    public async Task<string> ConnectNetworkAsync(RealtimeJoinHandoffPayload handoff)
    {
        bool acquired = false;
        CancellationTokenSource? connection = null;
        ClientNetworkingManager? attemptClient = null;
        try
        {
            // Do not let a second handoff wait behind an upgrade and resurrect the
            // connection after an explicit suspension cancels the first attempt.
            if (_networkConnect is not null)
                throw new InvalidOperationException("BrowserNetwork.AlreadyConnecting: wait for the current admission attempt to finish.");
            await _lifecycle.WaitAsync();
            acquired = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_running || !BrowserEngineExports.IsNetworkPageActive || _canvas is { Surface.CanRender: false })
                throw new InvalidOperationException("BrowserNetwork.InactiveWorld: join requires an active engine world and page.");
            if (_networkClient is not null || Engine.Networking is not null)
                throw new InvalidOperationException("BrowserNetwork.AlreadyConnected: disconnect before supplying a new handoff.");
            int epoch = Volatile.Read(ref _epoch);
            connection = new CancellationTokenSource();
            _networkConnect = connection;
            _networkState = "connecting";
            ClientNetworkingManager client = await Engine.ConnectWebSocketClientAsync(handoff, connection.Token,
                created => _networkClient = attemptClient = created);
            connection.Token.ThrowIfCancellationRequested();
            if (epoch != Volatile.Read(ref _epoch))
                throw new OperationCanceledException("Browser network startup was superseded.");
            _networkClient = client;
            _networkState = "authenticating";
            return NetworkState;
        }
        catch
        {
            // Validation of a second handoff must not disturb a healthy client.
            if (connection is not null)
            {
                _networkState = connection.IsCancellationRequested ? "suspended" : "failed";
                if (ReferenceEquals(_networkClient, attemptClient))
                    _networkClient = null;
            }
            throw;
        }
        finally
        {
            handoff.AdmissionSecret = null;
            handoff.SessionToken = null;
            if (ReferenceEquals(_networkConnect, connection))
                _networkConnect = null;
            connection?.Dispose();
            if (acquired)
                _lifecycle.Release();
        }
    }

    /// <summary>Invalidates pending upgrades and discards the old manager before any world/service teardown.</summary>
    public void SuspendNetwork()
    {
        bool owned = _networkConnect is not null || _networkClient is not null;
        ClientNetworkingManager? client = _networkClient;
        _networkConnect?.Cancel();
        if (!owned)
            return;
        _networkState = "suspended";
        _networkClient = null;
        if (_engineInitialized && client is not null)
            Engine.SuspendWebSocketClient(client);
    }

    private void ObserveNetworkFailure()
    {
        if (_networkClient is not { } client
            || ReferenceEquals(Engine.Networking, client)
                && client.WebSocketTransportFailure is null && client.ManagedTransportFailure is null)
            return;
        SuspendNetwork();
        _networkState = "failed";
    }
}
