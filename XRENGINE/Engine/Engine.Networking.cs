using System.IO;
using System.Net;
using System.Threading.Tasks;
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
        private static bool _environmentRealtimeHandoffApplied;

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
            bool result;
            if (runVRInPlace)
            {
                var window = _windows.FirstOrDefault();

                // OpenXR can be initialized without OpenVR manifests.
                // OpenVR requires both the action manifest and vrmanifest.
                if (vrSettings.VRRuntime == EVRRuntime.OpenXR)
                {
                    result = VRState.InitializeOpenXR(window);
                    if (!result)
                        Debug.LogWarning("Failed to initialize OpenXR (forced). VR will not be started.");
                }
                else if (vrSettings.VRRuntime == EVRRuntime.OpenVR)
                {
                    if (vrSettings.VRManifest is null || vrSettings.ActionManifest is null)
                    {
                        Debug.LogWarning("VR settings are not properly initialized for OpenVR. VR will not be started.");
                        return false;
                    }

                    result = await VRState.InitializeLocal(vrSettings.ActionManifest, vrSettings.VRManifest, window ?? _windows[0]);
                }
                else
                {
                    // Auto: try OpenXR first, then fall back to OpenVR if configured.
                    result = VRState.InitializeOpenXR(window);
                    if (!result)
                    {
                        if (vrSettings.VRManifest is null || vrSettings.ActionManifest is null)
                        {
                            Debug.LogWarning("VR settings are not properly initialized. VR will not be started.");
                            return false;
                        }

                        result = await VRState.InitializeLocal(vrSettings.ActionManifest, vrSettings.VRManifest, window ?? _windows[0]);
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

                if (vrSettings.VRManifest is null || vrSettings.ActionManifest is null)
                {
                    Debug.LogWarning("VR settings are not properly initialized. VR will not be started.");
                    return false;
                }

                result = await VRState.IninitializeClient(vrSettings.ActionManifest, vrSettings.VRManifest);
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
        ///   <item><description><b>P2PClient:</b> Peer-to-peer networking client</description></item>
        /// </list>
        /// </remarks>
        private static void InitializeNetworking(GameStartupSettings startupSettings)
        {
            if (Networking is BaseNetworkingManager previousNet)
            {
                previousNet.RemoteJobRequestReceived -= HandleRemoteJobRequestAsync;
            }

            if (!_environmentRealtimeHandoffApplied
                && RealtimeJoinHandoff.TryApplyFromEnvironment(startupSettings, out _, out string? handoffSource))
            {
                _environmentRealtimeHandoffApplied = true;
                Debug.Networking("[Realtime Handoff] Applied join payload from {0}.", handoffSource ?? "<unknown>");
            }

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
                    var server = new ServerNetworkingManager();
                    Networking = server;
                    server.Start(
                        IPAddress.Parse(startupSettings.UdpMulticastGroupIP),
                        startupSettings.UdpMulticastPort,
                        startupSettings.UdpServerBindPort);
                    break;
                case ENetworkingType.Client:
                    var client = new ClientNetworkingManager
                    {
                        SessionId = startupSettings.MultiplayerSessionId,
                        SessionToken = startupSettings.MultiplayerSessionToken,
                    };
                    Networking = client;
                    client.Start(
                        IPAddress.Parse(startupSettings.UdpMulticastGroupIP),
                        startupSettings.UdpMulticastPort,
                        ResolveNetworkAddress(startupSettings.ServerIP),
                        startupSettings.UdpServerSendPort,
                        startupSettings.UdpClientRecievePort);
                    break;
                case ENetworkingType.P2PClient:
                    var p2pClient = new PeerToPeerNetworkingManager();
                    Networking = p2pClient;
                    p2pClient.Start(
                        IPAddress.Parse(startupSettings.UdpMulticastGroupIP),
                        startupSettings.UdpMulticastPort,
                        ResolveNetworkAddress(startupSettings.ServerIP));
                    break;
            }

            if (Networking is BaseNetworkingManager net)
            {
                Jobs.RemoteTransport = new RemoteJobNetworkingTransport(net);
                net.RemoteJobRequestReceived += HandleRemoteJobRequestAsync;
            }
            else
            {
                Jobs.RemoteTransport = null;
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
            XRWorldInstance? worldInstance = ResolvePrimaryWorldInstance();
            return worldInstance?.TargetWorld is null
                ? null
                : WorldAssetIdentityProvider.Create(worldInstance.TargetWorld, RealtimeJoinHandoff.CurrentProtocolVersion);
        }

        private static XRWorldInstance? ResolvePrimaryWorldInstance()
        {
            foreach (var window in Windows)
            {
                if (window?.TargetWorldInstance is not null)
                    return window.TargetWorldInstance;
            }

            return XRWorldInstance.WorldInstances.Values.FirstOrDefault();
        }

        #endregion

        #region Remote Job Handling

        /// <summary>
        /// Handles incoming remote job requests from the network.
        /// </summary>
        private static Task<RemoteJobResponse?> HandleRemoteJobRequestAsync(RemoteJobRequest request)
            => HandleRemoteJobRequestInternalAsync(request);

        /// <summary>
        /// Internal implementation for processing remote job requests.
        /// </summary>
        private static async Task<RemoteJobResponse?> HandleRemoteJobRequestInternalAsync(RemoteJobRequest request)
        {
            if (request is null)
                return null;

            return request.Operation switch
            {
                RemoteJobRequest.Operations.AssetLoad => await HandleRemoteAssetLoadAsync(request).ConfigureAwait(false),
                _ => RemoteJobResponse.FromError(request.JobId, $"Unsupported remote job operation '{request.Operation}'."),
            };
        }

        /// <summary>
        /// Handles remote asset load requests by resolving the asset and returning its bytes.
        /// </summary>
        private static async Task<RemoteJobResponse?> HandleRemoteAssetLoadAsync(RemoteJobRequest request)
        {
            string? path = null;
            request.Metadata?.TryGetValue("path", out path);
            Guid assetId = Guid.Empty;
            if (request.Metadata?.TryGetValue("id", out var idText) == true)
                Guid.TryParse(idText, out assetId);

            try
            {
                byte[]? payload = null;
                string? resolvedPath = null;

                if (request.TransferMode == RemoteJobTransferMode.PushDataToRemote && request.Payload is { Length: > 0 })
                {
                    payload = request.Payload;
                }
                else if (assetId != Guid.Empty)
                {
                    if (Assets.TryGetAssetByID(assetId, out var existing) && !string.IsNullOrWhiteSpace(existing.FilePath) && File.Exists(existing.FilePath))
                    {
                        resolvedPath = existing.FilePath;
                        payload = await File.ReadAllBytesAsync(existing.FilePath).ConfigureAwait(false);
                    }
                    else if (Assets.TryResolveAssetPathById(assetId, out var resolvedByMeta) && !string.IsNullOrWhiteSpace(resolvedByMeta) && File.Exists(resolvedByMeta))
                    {
                        resolvedPath = resolvedByMeta;
                        payload = await File.ReadAllBytesAsync(resolvedByMeta).ConfigureAwait(false);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    resolvedPath = path;
                    payload = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                }

                if (payload is null)
                    return RemoteJobResponse.FromError(request.JobId, assetId != Guid.Empty
                        ? $"Asset not found for remote load with id '{assetId}'."
                        : $"Asset not found for remote load at '{path}'.");

                IReadOnlyDictionary<string, string>? responseMetadata = null;
                if (!string.IsNullOrWhiteSpace(resolvedPath))
                {
                    responseMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["path"] = resolvedPath,
                    };
                }

                return new RemoteJobResponse
                {
                    JobId = request.JobId,
                    Success = true,
                    Payload = payload,
                    Metadata = responseMetadata,
                    SenderId = Networking is BaseNetworkingManager net ? net.LocalPeerId : null,
                    TargetId = request.SenderId,
                };
            }
            catch (Exception ex)
            {
                return new RemoteJobResponse
                {
                    JobId = request.JobId,
                    Success = false,
                    Error = ex.Message,
                    SenderId = Networking is BaseNetworkingManager net ? net.LocalPeerId : null,
                    TargetId = request.SenderId,
                };
            }
        }

        #endregion
    }
}
