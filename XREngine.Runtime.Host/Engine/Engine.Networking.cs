using System.IO;
using System.Net;
using System.Threading.Tasks;
using XREngine.Input;
using XREngine.Networking;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine
{
    /// <summary>
    /// Networking, VR initialization, and remote job handling for the engine.
    /// </summary>
    public static partial class Engine
    {
        private static Func<RemoteJobRequest, Task<RemoteJobResponse?>>? _remoteAssetRequestHandler;
        #region VR Initialization

        /// <summary>
        /// Initializes VR subsystem based on startup settings.
        /// </summary>
        /// <param name="vrSettings">VR-specific startup settings including manifests and runtime preference.</param>
        /// <param name="runVRInPlace">If <c>true</c>, runs VR locally; otherwise uses client mode.</param>
        /// <returns><c>true</c> if VR initialization succeeded.</returns>
        /// <remarks>
        /// Supports OpenXR and OpenVR runtimes. In Auto mode, tries OpenXR first then falls back to OpenVR.
        /// </remarks>
        private static async Task<bool> InitializeVR(IVRGameStartupSettings vrSettings, bool runVRInPlace)
        {
            RuntimeOpenVrApplicationManifest? vrManifest = vrSettings.VRManifest;
            IRuntimeOpenVrActionManifest? actionManifest = vrSettings.ActionManifest;

            bool result;
            if (runVRInPlace)
            {
                var window = RuntimeEngine.Windows.FirstOrDefault();

                // OpenXR can be initialized without OpenVR manifests.
                // OpenVR requires both the action manifest and vrmanifest.
                if (vrSettings.VRRuntime == EVRRuntime.OpenXR)
                {
                    result = RuntimeEngine.VRState.InitializeOpenXR(window);
                    if (!result)
                        Debug.LogWarning("Failed to initialize OpenXR (forced). VR will not be started.");
                }
                else if (vrSettings.VRRuntime == EVRRuntime.OpenVR)
                {
                    if (vrManifest is null || actionManifest is null)
                    {
                        Debug.LogWarning("VR settings are not properly initialized for OpenVR. VR will not be started.");
                        return false;
                    }

                    result = await RuntimeEngine.VRState.InitializeLocal(actionManifest, vrManifest, window ?? RuntimeEngine.Windows[0]);
                }
                else
                {
                    // Auto: try OpenXR first, then fall back to OpenVR if configured.
                    result = RuntimeEngine.VRState.InitializeOpenXR(window);
                    if (!result)
                    {
                        if (vrManifest is null || actionManifest is null)
                        {
                            Debug.LogWarning("VR settings are not properly initialized. VR will not be started.");
                            return false;
                        }

                        result = await RuntimeEngine.VRState.InitializeLocal(actionManifest, vrManifest, window ?? RuntimeEngine.Windows[0]);
                    }
                }
            }
            else
            {
                // Client mode currently only supports OpenVR-based transport.
                if (vrSettings.VRRuntime == EVRRuntime.OpenXR)
                {
                    Debug.LogWarning("OpenXR is not supported in client VR mode. VR will not be started.");
                    return false;
                }

                if (vrManifest is null || actionManifest is null)
                {
                    Debug.LogWarning("VR settings are not properly initialized. VR will not be started.");
                    return false;
                }

                result = await RuntimeEngine.VRState.IninitializeClient(actionManifest, vrManifest);
            }

            return result;
        }

        #endregion

        #region Networking Initialization

        /// <summary>
        /// Initializes the networking subsystem based on startup settings.
        /// </summary>
        /// <remarks>
        /// Creates the appropriate networking manager based on <see cref="GameStartupSettings.NetworkingType"/>:
        /// <list type="bullet">
        ///   <item><description><b>Local:</b> No networking (single-player)</description></item>
        ///   <item><description><b>Server:</b> Authoritative server for client-server architecture</description></item>
        ///   <item><description><b>Client:</b> Client connecting to a dedicated server</description></item>
        /// </list>
        /// </remarks>
        private static void InitializeNetworking(GameStartupSettings startupSettings)
        {
            ShutdownNetworking();

            RuntimeEngineStartupPolicyServices.Require().PrepareNetworking(startupSettings);

            WorldAssetIdentity? localWorldAsset = ResolveLocalWorldAsset();
            RealtimeJoinHandoff.ValidateClientStartup(startupSettings, localWorldAsset, RealtimeJoinHandoff.CurrentProtocolVersion);
            RealtimeJoinHandoff.LogStartupSummary(startupSettings, localWorldAsset, RealtimeJoinHandoff.CurrentProtocolVersion);

            var appType = startupSettings.NetworkingType;
            switch (appType)
            {
                default:
                case ENetworkingType.Local:
                    Networking = null;
                    break;
                case ENetworkingType.Server:
                    var server = new ServerNetworkingManager
                    {
                        RequireManagedUdpTransport = ServerRequiresManagedUdpTransport,
                        EnableKinematicCharacterLocomotion = ServerRequiresManagedUdpTransport,
                    };
                    server.MaxPlayers = ServerMaximumPlayers ?? int.MaxValue;
                    Networking = server;
                    server.Start(
                        IPAddress.Parse(startupSettings.UdpMulticastGroupIP),
                        startupSettings.UdpMulticastPort,
                        startupSettings.UdpServerBindPort,
                        ServerBindAddress);
                    break;
                case ENetworkingType.Client:
                    var client = new ClientNetworkingManager
                    {
                        SessionId = startupSettings.MultiplayerSessionId,
                        SessionToken = startupSettings.MultiplayerSessionToken,
                        AccountId = startupSettings.MultiplayerAccountId,
                        ReservationId = startupSettings.MultiplayerReservationId,
                        AdmissionSecret = startupSettings.MultiplayerAdmissionSecret,
                        StableClientId = startupSettings.MultiplayerClientId,
                        WorkerGeneration = startupSettings.MultiplayerWorkerGeneration,
                        ResumeRequested = startupSettings.MultiplayerResumeRequested,
                        CredentialEpoch = startupSettings.MultiplayerCredentialEpoch,
                        Transport = startupSettings.MultiplayerTransport,
                        TlsServerName = startupSettings.ServerIP,
                        DevelopmentTlsCertificatePin = Environment.GetEnvironmentVariable("XRE_DEVELOPMENT_TLS_SPKI_SHA256"),
                    };
                    Networking = client;
                    client.Start(
                        IPAddress.Parse(startupSettings.UdpMulticastGroupIP),
                        startupSettings.UdpMulticastPort,
                        ResolveNetworkAddress(startupSettings.ServerIP),
                        startupSettings.UdpServerSendPort,
                        startupSettings.UdpClientRecievePort);
                    break;
            }

            if (Networking is BaseNetworkingManager net)
            {
                Jobs.RemoteTransport = new RemoteJobNetworkingTransport(net);
                _remoteAssetRequestHandler = request => HandleRemoteJobRequestAsync(request, net);
                net.RemoteJobRequestReceived += _remoteAssetRequestHandler;
            }
            else
            {
                Jobs.RemoteTransport = null;
            }
        }

        private static void ShutdownNetworking()
        {
            IRemoteJobTransport? remoteTransport = Jobs.RemoteTransport;
            Jobs.RemoteTransport = null;

            if (remoteTransport is IDisposable disposableTransport)
            {
                try
                {
                    disposableTransport.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.NetworkingWarning("[Net] Failed to dispose remote job transport during shutdown: {0}", ex.Message);
                }
            }

            if (Networking is not BaseNetworkingManager net)
                return;

            if (_remoteAssetRequestHandler is { } handler)
                net.RemoteJobRequestReceived -= handler;
            _remoteAssetRequestHandler = null;

            try
            {
                net.Dispose();
            }
            catch (Exception ex)
            {
                Debug.NetworkingWarning("[Net] Failed to dispose networking manager during shutdown: {0}", ex.Message);
            }
            finally
            {
                if (ReferenceEquals(Networking, net))
                    Networking = null;
            }
        }

        private static IPAddress ResolveNetworkAddress(string hostOrIp)
        {
            if (IPAddress.TryParse(hostOrIp, out IPAddress? parsedAddress))
                return parsedAddress;

            IPAddress[] resolved = Dns.GetHostAddresses(hostOrIp);
            if (resolved.Length == 0)
                throw new InvalidOperationException($"Unable to resolve network host '{hostOrIp}'.");

            return resolved[0];
        }

        private static WorldAssetIdentity? ResolveLocalWorldAsset()
        {
            RuntimeWorld? worldInstance = ResolvePrimaryWorldInstance();
            return worldInstance?.TargetWorld is null
                ? null
                : WorldAssetIdentityProvider.Create(worldInstance.TargetWorld, RealtimeJoinHandoff.CurrentProtocolVersion);
        }

        private static RuntimeWorld? ResolvePrimaryWorldInstance()
        {
            foreach (var window in RuntimeEngine.Windows)
            {
                if (window?.TargetWorldInstance?.WorldContext is RuntimeWorld worldInstance)
                    return worldInstance;
            }

            return RuntimeWorldRegistryServices.Current?.Snapshot().Values.FirstOrDefault();
        }

        #endregion

        #region Remote Job Handling

        /// <summary>
        /// Handles incoming remote job requests from the network.
        /// </summary>
        private static Task<RemoteJobResponse?> HandleRemoteJobRequestAsync(RemoteJobRequest request, BaseNetworkingManager networkOwner)
            => HandleRemoteJobRequestInternalAsync(request, networkOwner);

        /// <summary>
        /// Internal implementation for processing remote job requests.
        /// </summary>
        private static async Task<RemoteJobResponse?> HandleRemoteJobRequestInternalAsync(RemoteJobRequest request, BaseNetworkingManager networkOwner)
        {
            if (request is null)
                return null;

            return request.Operation switch
            {
                RemoteJobRequest.Operations.AssetLoad => await HandleRemoteAssetLoadAsync(request, networkOwner).ConfigureAwait(false),
                _ => RemoteJobResponse.FromError(request.JobId, $"Unsupported remote job operation '{request.Operation}'."),
            };
        }

        /// <summary>
        /// Handles remote asset load requests by resolving the asset and returning its bytes.
        /// </summary>
        private static async Task<RemoteJobResponse?> HandleRemoteAssetLoadAsync(RemoteJobRequest request, BaseNetworkingManager networkOwner)
        {
            AssetManager owner = Assets;
            string? localPeerId = networkOwner.LocalPeerId;
            string? path = null;
            request.Metadata?.TryGetValue("path", out path);
            Guid assetId = Guid.Empty;
            if (request.Metadata?.TryGetValue("id", out var idText) == true)
                Guid.TryParse(idText, out assetId);

            try
            {
                if (request.TransferMode == RemoteJobTransferMode.PushDataToRemote && request.Payload is { Length: > 0 })
                    return BuildRemoteAssetResponse(request, request.Payload, null, localPeerId);

                RemoteJobResponse? response = await owner.ServeRemoteAssetAsync(assetId, path,
                    (payload, resolvedPath) => BuildRemoteAssetResponse(request, payload, resolvedPath, localPeerId))
                    .ConfigureAwait(false);
                if (response is null)
                    return RemoteJobResponse.FromError(request.JobId, assetId != Guid.Empty
                        ? $"Asset not found for remote load with id '{assetId}'."
                        : $"Asset not found for remote load at '{path}'.");
                return response;
            }
            catch (Exception ex)
            {
                return new RemoteJobResponse
                {
                    JobId = request.JobId,
                    Success = false,
                    Error = ex.Message,
                    SenderId = localPeerId,
                    TargetId = request.SenderId,
                };
            }
        }

        private static RemoteJobResponse BuildRemoteAssetResponse(
            RemoteJobRequest request, byte[] payload, string? resolvedPath, string? localPeerId)
        {
            IReadOnlyDictionary<string, string>? responseMetadata = string.IsNullOrWhiteSpace(resolvedPath)
                ? null
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["path"] = resolvedPath };
            return new RemoteJobResponse
            {
                JobId = request.JobId,
                Success = true,
                Payload = payload,
                Metadata = responseMetadata,
                SenderId = localPeerId,
                TargetId = request.SenderId,
            };
        }

        #endregion
    }
}
