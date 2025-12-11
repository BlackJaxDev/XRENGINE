using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using XREngine;
using XREngine.Diagnostics;
using static XREngine.Engine;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static partial class UserInterface
    {
        private static GameStartupSettings.ENetworkingType _netMode = GameStartupSettings.ENetworkingType.Local;
        private static string _netServerIp = "127.0.0.1";
        private static string _netMulticastGroupIp = "239.0.0.222";
        private static int _netMulticastPort = 5000;
        private static int _netServerSendPort = 5000;
        private static int _netClientReceivePort = 5001;
        private static string _kickReason = "Kicked by operator";

        private static void DrawNetworkingPanel()
        {
            if (!_showNetworking)
                return;

            ImGui.SetNextWindowSize(new Vector2(440, 520), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("Networking", ref _showNetworking))
            {
                ImGui.End();
                return;
            }

            DrawNetworkingControls();
            ImGui.Separator();
            DrawNetworkingStatus();
            ImGui.Separator();
            DrawNetworkingConnections();

            ImGui.End();
        }

        private static void DrawNetworkingControls()
        {
            ImGui.Text("Mode");
            ImGui.SameLine();
            if (ImGui.BeginCombo("##NetMode", _netMode.ToString()))
            {
                foreach (GameStartupSettings.ENetworkingType val in Enum.GetValues(typeof(GameStartupSettings.ENetworkingType)))
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
            ImGui.InputInt("Server Send Port", ref _netServerSendPort);
            ImGui.InputInt("Client Receive Port", ref _netClientReceivePort);

            if (ImGui.Button("Start / Apply", new Vector2(-1, 0)))
                ApplyNetworking();

            if (ImGui.Button("Disconnect", new Vector2(-1, 0)))
                StopNetworking();
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
                        ImGui.Text(player.LocalPlayerIndex.ToString());
                        ImGui.TableSetColumnIndex(1);
                        ImGui.Text(player.PlayerInfo.ServerIndex.ToString());
                        ImGui.TableSetColumnIndex(2);
                        ImGui.TextUnformatted(player.ControlledPawn?.Name ?? "(no pawn)");
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
            try
            {
                Engine.Networking?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.Out($"[UI] Failed to dispose previous networking: {ex.Message}");
            }

            GameStartupSettings settings = new()
            {
                NetworkingType = _netMode,
                UdpMulticastGroupIP = _netMulticastGroupIp,
                UdpMulticastPort = _netMulticastPort,
                UdpServerSendPort = _netServerSendPort,
                UdpClientRecievePort = _netClientReceivePort,
                ServerIP = _netServerIp
            };

            Engine.ConfigureNetworking(settings);
        }

        private static void StopNetworking()
        {
            try
            {
                Engine.Networking?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.Out($"[UI] Failed to dispose networking: {ex.Message}");
            }

            GameStartupSettings settings = new() { NetworkingType = GameStartupSettings.ENetworkingType.Local };
            Engine.ConfigureNetworking(settings);
        }
    }
}
