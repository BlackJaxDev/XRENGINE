using System.Net;
using XREngine.Networking;

namespace XREngine;

public static partial class Engine
{
    /// <summary>
    /// Consumes a trusted in-memory handoff for the caller-owned world. Completion
    /// starts managed admission; gameplay remains paused until the production baseline commits.
    /// </summary>
    public static async Task<ClientNetworkingManager> ConnectWebSocketClientAsync(
        RealtimeJoinHandoffPayload handoff, CancellationToken cancellationToken = default,
        Action<ClientNetworkingManager>? onCreated = null)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        ClientNetworkingManager? client = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_callerThreadSession || Environment.CurrentManagedThreadId != RuntimeEngine.WindowThreadId)
                throw new InvalidOperationException("Realtime WebSocket composition requires the active caller-owned engine thread.");
            if (Networking is not null)
                throw new InvalidOperationException("Stop the current network client before supplying fresh admission.");
            RuntimeWorld world = ResolvePrimaryWorldInstance()
                ?? throw new InvalidOperationException("Load the verified engine world before joining realtime networking.");
            if (world.TargetWorld is null
                || !WorldAssetIdentityProvider.TryGetVerifiedIdentity(world.TargetWorld, out WorldAssetIdentity? identity)
                || identity is null)
                throw new InvalidOperationException("Browser realtime networking requires an independently verified world package identity.");
            Uri endpoint = RealtimeJoinHandoff.ValidateWebSocketClientHandoff(handoff, identity,
                RealtimeJoinHandoff.CurrentProtocolVersion);
            if (NetworkTransportServices.Required is not IRealtimeWebSocketBackend)
                throw new NotSupportedException("The installed network backend does not support realtime WebSockets.");

            client = new ClientNetworkingManager
            {
                Transport = RealtimeTransportKind.WebSocket,
                SessionId = handoff.SessionId,
                AccountId = handoff.AccountId,
                StableClientId = handoff.ClientId,
                ReservationId = handoff.ReservationId,
                AdmissionSecret = handoff.AdmissionSecret,
                WorkerGeneration = handoff.WorkerGeneration,
                ResumeRequested = handoff.ResumeRequested,
                CredentialEpoch = handoff.CredentialEpoch,
            };
            world.PausePlay();
            Networking = client;
            // Publish exact ownership synchronously, before the upgrade can yield.
            // The host can then cancel only this client during page/world teardown.
            onCreated?.Invoke(client);
            // This is only the shared datagram contract's routing key. Browser
            // WebSocket I/O uses the validated URI and never contacts this IP.
            await client.StartWebSocketAsync(endpoint, new IPEndPoint(IPAddress.Loopback, endpoint.Port), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(Networking, client) || !_callerThreadSession)
                throw new OperationCanceledException("Realtime WebSocket startup was superseded.");
            return client;
        }
        catch
        {
            if (client is not null)
            {
                if (ReferenceEquals(Networking, client))
                    Networking = null;
                client.SuspendWebSocket();
                client.Dispose();
            }
            throw;
        }
        finally
        {
            handoff.AdmissionSecret = null;
            handoff.SessionToken = null;
        }
    }

    /// <summary>Aborts the caller-owned browser client; resuming always requires a new handoff and baseline.</summary>
    public static void SuspendWebSocketClient(ClientNetworkingManager client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!_callerThreadSession || !ReferenceEquals(Networking, client) || client.Transport != RealtimeTransportKind.WebSocket)
            return;
        Networking = null;
        try { client.SuspendWebSocket(); }
        finally { client.Dispose(); }
    }
}
