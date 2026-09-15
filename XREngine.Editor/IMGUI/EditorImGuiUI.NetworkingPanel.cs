using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using XREngine;
using XREngine.Components;
using XREngine.ControlPlane;
using XREngine.Diagnostics;
using XREngine.Networking;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using static XREngine.Engine;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
        private const uint ControlPlaneHandoffTextCapacity = 16 * 1024;

        private static readonly InMemoryControlPlane _editorControlPlane = new();
        private static readonly string[] _controlPlaneWorldEnvironmentNames =
        [
            XREngineEnvironmentVariables.WorldId,
            XREngineEnvironmentVariables.WorldRevision,
            XREngineEnvironmentVariables.WorldContentHash,
            XREngineEnvironmentVariables.WorldAssetSchemaVersion,
            XREngineEnvironmentVariables.WorldRequiredBuildVersion,
        ];

        private static bool _netSettingsSeeded;
        private static ENetworkingType _netMode = ENetworkingType.Local;
        private static string _netServerIp = "127.0.0.1";
        private static string _netMulticastGroupIp = "239.0.0.222";
        private static int _netMulticastPort = 5000;
        private static int _netServerBindPort = 5000;
        private static int _netServerSendPort = 5000;
        private static int _netClientReceivePort = 5001;
        private static string _netSessionId = string.Empty;
        private static string _netSessionToken = string.Empty;
        private static string _kickReason = "Kicked by operator";
        private static string _controlPlaneHostId = $"editor-{Environment.MachineName}";
        private static string _controlPlaneDisplayName = "Editor Collaboration";
        private static string _controlPlaneInstanceId = string.Empty;
        private static int _controlPlaneMaxPlayers = 4;
        private static string _controlPlaneHandoffJson = string.Empty;
        private static string _controlPlaneStatus = string.Empty;
        private static bool _controlPlaneResolverInstalled;
        private static Func<PlayerJoinRequest, ServerJoinAdmissionResult?>? _controlPlanePreviousResolver;
        private static Action<ServerSessionPlayerEvent>? _controlPlaneConnectedHandler;
        private static Action<ServerSessionPlayerEvent>? _controlPlaneDisconnectedHandler;
        private static int? _controlPlanePreviousMaximumPlayers;
        private static Dictionary<string, string?>? _controlPlaneOriginalWorldEnvironment;
        private static string _managedServiceUrl = "http://127.0.0.1:5088";
        private static string _managedServiceBearer = string.Empty;
        private static string _managedServicePackageId = string.Empty;
        private static string _managedServiceInstanceId = string.Empty;
        private static string _managedServiceClientId = $"editor-{Environment.MachineName}-{Guid.NewGuid():N}";
        private static string _managedServiceStatus = string.Empty;
        private static string _managedServiceDirectory = string.Empty;
        private static int _managedServiceMaxPlayers = 4;
        private static Task<XRWorld>? _managedServiceLaunchTask;
        private static Task? _managedServiceLeaveTask;
        private static Task<string>? _managedServiceOperationTask;
        private static CancellationTokenSource? _managedServiceCancellation;
        private static readonly ManagedClientJoinCoordinator _managedJoinCoordinator = new();

        private static void DrawNetworkingPanel()
        {
            if (!_showNetworking)
                return;

            EnsureNetworkingUiState();

            ImGui.SetNextWindowSize(new Vector2(620, 720), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("Networking", ref _showNetworking))
            {
                ImGui.End();
                return;
            }

            DrawNetworkingControls();
            ImGui.Separator();
            DrawManagedServiceControls();
            ImGui.Separator();
            DrawControlPlaneControls();
            ImGui.Separator();
            DrawNetworkingStatus();
            ImGui.Separator();
            DrawNetworkingConnections();

            ImGui.End();
        }

        private static void EnsureNetworkingUiState()
        {
            if (_netSettingsSeeded || GameSettings is not GameStartupSettings settings)
                return;

            _netMode = settings.NetworkingType;
            _netServerIp = settings.ServerIP;
            _netMulticastGroupIp = settings.UdpMulticastGroupIP;
            _netMulticastPort = settings.UdpMulticastPort;
            _netServerBindPort = settings.UdpServerBindPort;
            _netServerSendPort = settings.UdpServerSendPort;
            _netClientReceivePort = settings.UdpClientRecievePort;
            _netSessionId = settings.MultiplayerSessionId?.ToString("D") ?? string.Empty;
            _netSessionToken = settings.MultiplayerSessionToken ?? string.Empty;
            _netSettingsSeeded = true;
        }

        private static void DrawNetworkingControls()
        {
            ImGui.Text("Mode");
            ImGui.SameLine();
            if (ImGui.BeginCombo("##NetMode", _netMode.ToString()))
            {
                foreach (ENetworkingType val in Enum.GetValues(typeof(ENetworkingType)))
                {
                    bool selected = val == _netMode;
                    if (ImGui.Selectable(val.ToString(), selected))
                        _netMode = val;
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            ImGui.InputText("Server IP", ref _netServerIp, 64);
            ImGui.InputText("Multicast Group", ref _netMulticastGroupIp, 64);
            ImGui.InputInt("Multicast Port", ref _netMulticastPort);
            ImGui.InputInt("Server Bind Port", ref _netServerBindPort);
            ImGui.InputInt("Server Send Port", ref _netServerSendPort);
            ImGui.InputInt("Client Receive Port", ref _netClientReceivePort);
            ImGui.InputText("Session Id", ref _netSessionId, 64);
            ImGui.InputText("Session Token", ref _netSessionToken, 256);

            if (ImGui.Button("Start / Apply", new Vector2(-1, 0)))
                ApplyNetworking();

            if (ImGui.Button("Disconnect", new Vector2(-1, 0)))
                StopNetworking();
        }

        private static void DrawControlPlaneControls()
        {
            ImGui.TextUnformatted("Control Plane");
            ImGui.InputText("Host Id", ref _controlPlaneHostId, 128);
            ImGui.InputText("Instance Name", ref _controlPlaneDisplayName, 128);
            ImGui.InputInt("Instance Max Players", ref _controlPlaneMaxPlayers);

            if (ImGui.Button("Create / Start Editor Server Instance", new Vector2(-1, 0)))
                CreateAndStartControlPlaneServer();

            bool hasInstance = !string.IsNullOrWhiteSpace(_controlPlaneInstanceId);
            using (new ImGuiDisabledScope(!hasInstance))
            {
                if (ImGui.Button("Issue Client Handoff", new Vector2(-1, 0)))
                    IssueControlPlaneHandoff();
            }

            if (hasInstance)
                ImGui.TextUnformatted($"Instance: {_controlPlaneInstanceId}");

            ImGui.TextUnformatted("Handoff JSON");
            ImGui.InputTextMultiline(
                "##ControlPlaneHandoffJson",
                ref _controlPlaneHandoffJson,
                ControlPlaneHandoffTextCapacity,
                new Vector2(-1, 110),
                ImGuiInputTextFlags.AllowTabInput);

            using (new ImGuiDisabledScope(string.IsNullOrWhiteSpace(_controlPlaneHandoffJson)))
            {
                if (ImGui.Button("Copy Handoff", new Vector2(-1, 0)))
                    ImGui.SetClipboardText(_controlPlaneHandoffJson);

                if (ImGui.Button("Join From Handoff", new Vector2(-1, 0)))
                    JoinFromControlPlaneHandoff();
            }

            if (!string.IsNullOrWhiteSpace(_controlPlaneStatus))
                ImGui.TextWrapped(_controlPlaneStatus);
        }

        private static void DrawManagedServiceControls()
        {
            _managedJoinCoordinator.Observe();
            ImGui.TextUnformatted("Managed Service");
            ImGui.InputText("Service URL", ref _managedServiceUrl, 256);
            ImGui.InputText("Service Bearer", ref _managedServiceBearer, 512, ImGuiInputTextFlags.Password);
            ImGui.InputText("Service Package", ref _managedServicePackageId, 128);
            ImGui.InputText("Service Instance", ref _managedServiceInstanceId, 128);
            ImGui.InputText("Service Client", ref _managedServiceClientId, 128);
            ImGui.InputInt("Service Max Players", ref _managedServiceMaxPlayers);

            bool configured = Uri.TryCreate(_managedServiceUrl, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(_managedServiceBearer);
            bool busy = _managedServiceLaunchTask is not null || _managedServiceLeaveTask is not null || _managedServiceOperationTask is not null;
            using (new ImGuiDisabledScope(!configured || busy))
            {
                if (ImGui.Button("Browse Managed Service", new Vector2(-1, 0)))
                    _managedServiceOperationTask = BrowseManagedServiceAsync();
                if (ImGui.Button("Create Managed Instance", new Vector2(-1, 0)))
                    _managedServiceOperationTask = CreateManagedInstanceAsync();
                if (ImGui.Button("Reserve / Join Managed Instance", new Vector2(-1, 0)))
                {
                    _managedServiceCancellation?.Dispose();
                    _managedServiceCancellation = new CancellationTokenSource();
                    _managedServiceLaunchTask = ReserveManagedLaunchAsync(_managedServiceCancellation.Token);
                }
            }
            using (new ImGuiDisabledScope(!busy))
                if (ImGui.Button("Cancel Managed Service Request", new Vector2(-1, 0)))
                    _managedServiceCancellation?.Cancel();

            if (_managedServiceOperationTask is { IsCompleted: true } operation)
            {
                _managedServiceOperationTask = null;
                try
                {
                    string result = operation.GetAwaiter().GetResult();
                    if (result.StartsWith("{", StringComparison.Ordinal))
                    {
                        _managedServiceDirectory = result;
                        using JsonDocument document = JsonDocument.Parse(result);
                        if (document.RootElement.TryGetProperty("instanceId", out JsonElement id))
                            _managedServiceInstanceId = id.GetString() ?? _managedServiceInstanceId;
                        _managedServiceStatus = "Managed service operation completed.";
                    }
                    else
                        _managedServiceStatus = result;
                }
                catch (Exception exception) { _managedServiceStatus = $"Managed service operation failed: {exception.Message}"; }
            }
            if (_managedServiceLaunchTask is { IsCompleted: true } launchTask)
            {
                _managedServiceLaunchTask = null;
                _managedServiceCancellation?.Dispose();
                _managedServiceCancellation = null;
                try
                {
                    _ = launchTask.GetAwaiter().GetResult();
                    _managedServiceStatus = "Managed world applied; waiting for assignment and synchronization.";
                }
                catch (Exception exception) { _managedServiceStatus = $"Managed join failed: {exception.Message}"; }
            }
            if (_managedServiceLeaveTask is { IsCompleted: true } leaveTask)
            {
                _managedServiceLeaveTask = null;
                try { leaveTask.GetAwaiter().GetResult(); _managedServiceStatus = "Managed instance left."; }
                catch (Exception exception) { _managedServiceStatus = $"Managed leave failed: {exception.Message}"; }
            }

            if (!string.IsNullOrWhiteSpace(_managedServiceStatus))
                ImGui.TextWrapped(_managedServiceStatus);
            if (!string.IsNullOrWhiteSpace(_managedServiceDirectory))
                ImGui.InputTextMultiline("##ManagedServiceDirectory", ref _managedServiceDirectory, ControlPlaneHandoffTextCapacity, new Vector2(-1, 75), ImGuiInputTextFlags.ReadOnly);
            ManagedClientJoinStatus status = _managedJoinCoordinator.Status;
            ImGui.TextWrapped($"Managed client: {status.State} — {status.Message}");
            if (status.FailureKind != ManagedClientJoinFailureKind.None)
                ImGui.TextColored(new Vector4(1f, .5f, .35f, 1f), $"Outcome: {status.FailureKind}");
            if (status.State is not (ManagedClientJoinState.Idle or ManagedClientJoinState.Leaving))
                if (ImGui.Button("Leave Managed Instance", new Vector2(-1, 0)))
                    _managedServiceLeaveTask = _managedJoinCoordinator.LeaveAsync();
        }

        private static ManagedInstanceServiceClient CreateManagedServiceClient()
            => new(new Uri(_managedServiceUrl), _managedServiceBearer);

        private static async Task<string> BrowseManagedServiceAsync()
        {
            using ManagedInstanceServiceClient client = CreateManagedServiceClient();
            using JsonDocument packages = await client.ListPackagesAsync().ConfigureAwait(false);
            using JsonDocument instances = await client.ListInstancesAsync().ConfigureAwait(false);
            return JsonSerializer.Serialize(new { packages = packages.RootElement, instances = instances.RootElement }, new JsonSerializerOptions { WriteIndented = true });
        }

        private static async Task<string> CreateManagedInstanceAsync()
        {
            using ManagedInstanceServiceClient client = CreateManagedServiceClient();
            using JsonDocument created = await client.CreateInstanceAsync(new()
            {
                OperationId = Guid.NewGuid().ToString("N"),
                PackageId = _managedServicePackageId,
                DisplayName = string.IsNullOrWhiteSpace(_controlPlaneDisplayName) ? "Editor managed instance" : _controlPlaneDisplayName,
                MaxPlayers = Math.Max(1, _managedServiceMaxPlayers),
                IsPublic = true,
            }).ConfigureAwait(false);
            return created.RootElement.GetRawText();
        }

        private static async Task<XRWorld> ReserveManagedLaunchAsync(CancellationToken cancellationToken)
        {
            using ManagedInstanceServiceClient client = CreateManagedServiceClient();
            ManagedAdmissionReservation reservation = await client.ReserveAsync(_managedServiceInstanceId, new()
            {
                OperationId = Guid.NewGuid().ToString("N"),
                ClientId = _managedServiceClientId,
                BuildVersion = RealtimeJoinHandoff.CurrentProtocolVersion,
            }, cancellationToken).ConfigureAwait(false);
            var cache = new RemoteWorldPackageCache(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XREngine", "ManagedWorldCache"));
            for (int attempt = 0; attempt < 40; attempt++)
            {
                try { return await _managedJoinCoordinator.JoinAsync(client, _managedServiceInstanceId, reservation.ReservationId, cache, cancellationToken).ConfigureAwait(false); }
                catch (System.Net.Http.HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }
            throw new TimeoutException("The managed service did not deliver an admission before its retry window elapsed.");
        }


        private static void DrawNetworkingStatus()
        {
            var net = Engine.Networking;
            ImGui.TextUnformatted(net is null ? "Status: Local (no networking)" : $"Status: {net.GetType().Name}");
            if (net is null)
                return;

            ImGui.TextUnformatted($"Peer Id: {net.LocalPeerId}");
            ImGui.TextUnformatted($"RTT: {net.AverageRoundTripTimeMs} ms (smoothing {net.RTTSmoothingPercent:P0})");
            ImGui.TextUnformatted($"Data: {net.DataPerSecondString}, Packets/s: {net.PacketsPerSecond}");

            if (net is ClientNetworkingManager client && client.LocalWorldAsset is not null)
            {
                ImGui.Separator();
                ImGui.TextUnformatted($"World: {client.LocalWorldAsset.WorldId}");
                ImGui.TextUnformatted($"Revision: {client.LocalWorldAsset.RevisionId}");
                ImGui.TextUnformatted($"Hash: {client.LocalWorldAsset.ContentHash}");
            }
        }

        private static void DrawNetworkingConnections()
        {
            var net = Engine.Networking;
            if (net is ServerNetworkingManager server)
            {
                ImGui.Text("Connected Clients");
                ImGui.InputText("Kick Reason", ref _kickReason, 128);

                var connections = server.GetConnectionsSnapshot();
                if (ImGui.BeginTable("NetClients", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame))
                {
                    ImGui.TableSetupColumn("Slot");
                    ImGui.TableSetupColumn("Client Id");
                    ImGui.TableSetupColumn("Last Heard (s)");
                    ImGui.TableSetupColumn("Actions");
                    ImGui.TableHeadersRow();

                    double now = DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;
                    foreach (var entry in connections.OrderBy(static c => c.ServerPlayerIndex))
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.Text(entry.ServerPlayerIndex.ToString());
                        ImGui.TableSetColumnIndex(1);
                        ImGui.TextUnformatted(entry.ClientId);
                        ImGui.TableSetColumnIndex(2);
                        double age = now - entry.LastHeardUtc.Subtract(DateTime.UnixEpoch).TotalSeconds;
                        ImGui.Text($"{age:F1}");
                        ImGui.TableSetColumnIndex(3);
                        ImGui.PushID(entry.ServerPlayerIndex);
                        if (ImGui.SmallButton("Kick"))
                            server.KickClient(entry.ServerPlayerIndex, _kickReason);
                        ImGui.PopID();
                    }
                    ImGui.EndTable();
                }
            }
            else if (net is ClientNetworkingManager)
            {
                ImGui.Text("Local Players");
                if (ImGui.BeginTable("NetLocalPlayers", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Local Index");
                    ImGui.TableSetupColumn("Server Index");
                    ImGui.TableSetupColumn("Pawn");
                    ImGui.TableHeadersRow();

                    foreach (var player in Engine.State.LocalPlayers)
                    {
                        if (player is null)
                            continue;

                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.Text(player.LocalPlayerIndex?.ToString() ?? "<none>");
                        ImGui.TableSetColumnIndex(1);
                        ImGui.Text(player.PlayerInfo?.ServerIndex is int serverIndex ? serverIndex.ToString() : "<none>");
                        ImGui.TableSetColumnIndex(2);
                        ImGui.TextUnformatted((player.ControlledPawnComponent as PawnComponent)?.Name ?? "(no pawn)");
                    }
                    ImGui.EndTable();
                }
            }
            else
            {
                ImGui.TextDisabled("No network session active.");
            }
        }

        private static void ApplyNetworking()
        {
            if (_netMode != ENetworkingType.Server)
                ClearControlPlaneServerResolver();

            GameStartupSettings settings = CreateNetworkingSettingsFromUi();
            RestartNetworking(settings);
            _netSettingsSeeded = true;
        }

        private static void CreateAndStartControlPlaneServer()
        {
            try
            {
                RuntimeWorld? worldInstance = ResolveEditorWorldInstance();
                if (worldInstance?.TargetWorld is null)
                {
                    _controlPlaneStatus = "No editor world is loaded for the control-plane server.";
                    return;
                }

                int maxPlayers = Math.Max(1, _controlPlaneMaxPlayers);
                int bindPort = ClampUdpPort(_netServerBindPort, 5000);
                int advertisedPort = ClampUdpPort(_netServerSendPort, bindPort);
                _netServerBindPort = bindPort;
                _netServerSendPort = advertisedPort;
                _netServerIp = string.IsNullOrWhiteSpace(_netServerIp) ? "127.0.0.1" : _netServerIp.Trim();

                StopControlPlaneInstance();

                WorldAssetIdentity worldAsset = WorldAssetIdentityProvider.Create(
                    worldInstance.TargetWorld,
                    RealtimeJoinHandoff.CurrentProtocolVersion);

                ControlPlaneHostSnapshot host = _editorControlPlane.RegisterHost(new ControlPlaneHostRegistration
                {
                    HostId = string.IsNullOrWhiteSpace(_controlPlaneHostId) ? "editor-host" : _controlPlaneHostId.Trim(),
                    DisplayName = Environment.MachineName,
                    Endpoint = new RealtimeEndpointDescriptor
                    {
                        Host = _netServerIp,
                        Port = advertisedPort,
                        ProtocolVersion = RealtimeJoinHandoff.CurrentProtocolVersion,
                    },
                    MaxInstances = 1,
                    MaxPlayers = maxPlayers,
                    Metadata =
                    {
                        ["source"] = "editor",
                    },
                });

                ControlPlaneResult<MultiplayerInstanceInfo> create = _editorControlPlane.CreateInstance(new CreateMultiplayerInstanceRequest
                {
                    DisplayName = string.IsNullOrWhiteSpace(_controlPlaneDisplayName)
                        ? "Editor Collaboration"
                        : _controlPlaneDisplayName.Trim(),
                    HostId = host.HostId,
                    WorldAsset = worldAsset,
                    SessionId = Guid.TryParse(_netSessionId, out Guid requestedSessionId) ? requestedSessionId : null,
                    SessionToken = string.IsNullOrWhiteSpace(_netSessionToken) ? null : _netSessionToken,
                    MaxPlayers = maxPlayers,
                    Metadata =
                    {
                        ["createdBy"] = Environment.UserName,
                    },
                });

                if (!create.Success || create.Value is null)
                {
                    _controlPlaneStatus = create.Message ?? "Failed to create control-plane instance.";
                    return;
                }

                MultiplayerInstanceInfo instance = create.Value;
                _controlPlaneInstanceId = instance.InstanceId;
                _netMode = ENetworkingType.Server;
                _netSessionId = instance.SessionId.ToString("D");
                _netSessionToken = instance.SessionToken;
                _controlPlaneHandoffJson = SerializeHandoff(InMemoryControlPlane.CreateJoinPayload(instance));

                ApplyWorldAssetEnvironment(instance.WorldAsset);
                ClearControlPlaneServerResolver();
                InstallControlPlaneServerResolver(instance.InstanceId);
                RestartNetworking(CreateNetworkingSettingsFromUi());
                _netSettingsSeeded = true;
                _controlPlaneStatus = $"Started editor server instance '{instance.DisplayName ?? instance.InstanceId}' at {_netServerIp}:{_netServerBindPort}.";
            }
            catch (Exception ex)
            {
                _controlPlaneStatus = $"Failed to start control-plane server: {ex.Message}";
                Debug.Out($"[UI] Failed to start control-plane server: {ex}");
            }
        }

        private static void IssueControlPlaneHandoff()
        {
            try
            {
                ControlPlaneResult<MultiplayerInstanceInfo> result = _editorControlPlane.GetInstance(_controlPlaneInstanceId, includeToken: true);
                if (!result.Success || result.Value is null)
                {
                    _controlPlaneStatus = result.Message ?? "No control-plane instance is available.";
                    return;
                }

                _controlPlaneHandoffJson = SerializeHandoff(InMemoryControlPlane.CreateJoinPayload(result.Value));
                _controlPlaneStatus = $"Issued handoff for instance '{result.Value.DisplayName ?? result.Value.InstanceId}'.";
            }
            catch (Exception ex)
            {
                _controlPlaneStatus = $"Failed to issue handoff: {ex.Message}";
                Debug.Out($"[UI] Failed to issue control-plane handoff: {ex}");
            }
        }

        private static void JoinFromControlPlaneHandoff()
        {
            try
            {
                RealtimeJoinHandoffPayload payload = JsonSerializer.Deserialize(
                    _controlPlaneHandoffJson,
                    XreControlPlaneJsonContext.Default.RealtimeJoinHandoffPayload)
                    ?? throw new InvalidOperationException("The handoff payload was empty.");

                if (payload.WorldAsset is not null)
                    ApplyWorldAssetEnvironment(payload.WorldAsset);

                GameStartupSettings settings = CreateNetworkingSettingsFromUi();
                RealtimeJoinHandoff.ApplyToSettings(settings, payload);
                settings.UdpClientRecievePort = ClampUdpPort(_netClientReceivePort, 5001);

                _netMode = settings.NetworkingType;
                _netServerIp = settings.ServerIP;
                _netServerSendPort = settings.UdpServerSendPort;
                _netSessionId = settings.MultiplayerSessionId?.ToString("D") ?? string.Empty;
                _netSessionToken = settings.MultiplayerSessionToken ?? string.Empty;
                ClearControlPlaneServerResolver();
                RestartNetworking(settings);
                _netSettingsSeeded = true;
                _controlPlaneStatus = $"Joined handoff at {settings.ServerIP}:{settings.UdpServerSendPort}.";
            }
            catch (JsonException ex)
            {
                _controlPlaneStatus = $"Handoff JSON is invalid: {ex.Message}";
            }
            catch (Exception ex)
            {
                _controlPlaneStatus = $"Failed to join handoff: {ex.Message}";
                Debug.Out($"[UI] Failed to join control-plane handoff: {ex}");
            }
        }

        private static void RestartNetworking(GameStartupSettings settings)
        {
            try
            {
                Engine.Networking?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.Out($"[UI] Failed to dispose previous networking: {ex.Message}");
            }

            Engine.ConfigureNetworking(settings);
        }

        private static GameStartupSettings CreateNetworkingSettingsFromUi()
            => new()
            {
                NetworkingType = _netMode,
                UdpMulticastGroupIP = _netMulticastGroupIp,
                UdpMulticastPort = _netMulticastPort,
                UdpServerBindPort = ClampUdpPort(_netServerBindPort, 5000),
                UdpServerSendPort = ClampUdpPort(_netServerSendPort, 5000),
                UdpClientRecievePort = ClampUdpPort(_netClientReceivePort, 5001),
                ServerIP = _netServerIp,
                MultiplayerSessionId = Guid.TryParse(_netSessionId, out Guid parsedSessionId) ? parsedSessionId : null,
                MultiplayerSessionToken = string.IsNullOrWhiteSpace(_netSessionToken) ? null : _netSessionToken,
            };

        private static void StopNetworking()
        {
            ClearControlPlaneServerResolver();
            StopControlPlaneInstance();
            RestoreWorldAssetEnvironment();

            try
            {
                Engine.Networking?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.Out($"[UI] Failed to dispose networking: {ex.Message}");
            }

            GameStartupSettings settings = new()
            {
                NetworkingType = ENetworkingType.Local,
            };

            Engine.ConfigureNetworking(settings);
            _netSettingsSeeded = true;
        }

        private static void StopControlPlaneInstance()
        {
            if (string.IsNullOrWhiteSpace(_controlPlaneInstanceId))
                return;

            _editorControlPlane.StopInstance(_controlPlaneInstanceId);
            _controlPlaneInstanceId = string.Empty;
        }

        private static void InstallControlPlaneServerResolver(string instanceId)
        {
            _controlPlanePreviousResolver = Engine.ServerJoinAdmissionResolver;
            _controlPlanePreviousMaximumPlayers = Engine.ServerMaximumPlayers;
            Engine.ServerMaximumPlayers = _editorControlPlane.GetInstance(instanceId).Value?.MaxPlayers;
            Engine.ServerJoinAdmissionResolver = request =>
            {
                ControlPlaneResult<MultiplayerInstanceInfo> instanceResult = _editorControlPlane.GetInstance(instanceId, includeToken: true);
                if (!instanceResult.Success || instanceResult.Value is null)
                    return new ServerJoinAdmissionResult(null, AdmissionFailureReason.SessionNotFound, instanceResult.Message);

                MultiplayerInstanceInfo instance = instanceResult.Value;
                if (request.SessionId is null)
                    return new ServerJoinAdmissionResult(null, AdmissionFailureReason.InvalidRequest, "A control-plane session id is required.");

                AdmissionFailureReason sessionFailure = RealtimeAdmissionValidator.ValidateSession(
                    request,
                    instance.SessionId,
                    instance.SessionToken,
                    out string sessionMessage);
                if (sessionFailure != AdmissionFailureReason.None)
                    return new ServerJoinAdmissionResult(null, sessionFailure, sessionMessage);

                RuntimeWorld? worldInstance = ResolveEditorWorldInstance();
                if (worldInstance is null)
                    return new ServerJoinAdmissionResult(null, AdmissionFailureReason.SessionNotFound, "No editor world instance is loaded.");

                return new ServerJoinAdmissionResult(new ServerSessionContext(
                    instance.SessionId,
                    worldInstance,
                    instance.WorldAsset));
            };
            // Record occupancy only after runtime world validation and pawn/controller creation.
            // Failed admission therefore never leaves a player in the editor's directory.
            _controlPlaneConnectedHandler = player =>
            {
                MultiplayerInstanceInfo? instance = _editorControlPlane.GetInstance(instanceId).Value;
                if (instance is null || instance.SessionId != player.SessionId)
                    return;
                var joined = _editorControlPlane.JoinInstance(new JoinMultiplayerInstanceRequest
                {
                    InstanceId = instanceId, ClientId = player.ClientId,
                    LocalWorldAsset = instance.WorldAsset, BuildVersion = instance.Endpoint.ProtocolVersion,
                });
                if (!joined.Success && Engine.Networking is ServerNetworkingManager server)
                    server.KickClient(player.ServerPlayerIndex, "Editor admission accounting rejected the connection.");
            };
            _controlPlaneDisconnectedHandler = player =>
            {
                if (_editorControlPlane.GetInstance(instanceId).Value?.SessionId == player.SessionId)
                    _editorControlPlane.LeaveInstance(new LeaveMultiplayerInstanceRequest
                    {
                        InstanceId = instanceId,
                        ClientId = player.ClientId,
                    });
            };
            Engine.ServerPlayerConnected += _controlPlaneConnectedHandler;
            Engine.ServerPlayerDisconnected += _controlPlaneDisconnectedHandler;
            _controlPlaneResolverInstalled = true;
        }

        private static void ClearControlPlaneServerResolver()
        {
            if (!_controlPlaneResolverInstalled)
                return;

            Engine.ServerJoinAdmissionResolver = _controlPlanePreviousResolver;
            Engine.ServerMaximumPlayers = _controlPlanePreviousMaximumPlayers;
            Engine.ServerPlayerConnected -= _controlPlaneConnectedHandler;
            Engine.ServerPlayerDisconnected -= _controlPlaneDisconnectedHandler;
            _controlPlanePreviousResolver = null;
            _controlPlaneConnectedHandler = null;
            _controlPlaneDisconnectedHandler = null;
            _controlPlaneResolverInstalled = false;
        }

        private static RuntimeWorld? ResolveEditorWorldInstance()
        {
            foreach (var window in RuntimeEngine.Windows)
            {
                if (window?.TargetWorldInstance?.WorldContext is RuntimeWorld worldInstance)
                    return worldInstance;
            }

            return Engine.WorldInstances.FirstOrDefault();
        }

        private static void ApplyWorldAssetEnvironment(WorldAssetIdentity asset)
        {
            _controlPlaneOriginalWorldEnvironment ??= _controlPlaneWorldEnvironmentNames.ToDictionary(
                static name => name,
                static name => Environment.GetEnvironmentVariable(name),
                StringComparer.Ordinal);

            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.WorldId, asset.WorldId);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.WorldRevision, asset.RevisionId);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.WorldContentHash, asset.ContentHash);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.WorldAssetSchemaVersion, asset.AssetSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.WorldRequiredBuildVersion, asset.RequiredBuildVersion);
        }

        private static void RestoreWorldAssetEnvironment()
        {
            if (_controlPlaneOriginalWorldEnvironment is null)
                return;

            foreach (var entry in _controlPlaneOriginalWorldEnvironment)
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);

            _controlPlaneOriginalWorldEnvironment = null;
        }

        private static int ClampUdpPort(int port, int fallback)
            => port is >= 1 and <= 65535 ? port : fallback;

        private static string SerializeHandoff(RealtimeJoinHandoffPayload payload)
            => JsonSerializer.Serialize(payload, XreControlPlaneJsonContext.Default.RealtimeJoinHandoffPayload);
}
