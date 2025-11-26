using ImGuiNET;
using System.Linq;
using System.Numerics;
using XREngine;
using XREngine.Input;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static partial class UserInterface
    {
        private static void DrawStatePanel()
        {
            if (!_showStatePanel) return;
            if (!ImGui.Begin("Engine State", ref _showStatePanel))
            {
                ImGui.End();
                return;
            }

            DrawPlayModeSection();
            ImGui.Separator();
            DrawPlayersSection();
            ImGui.Separator();
            DrawWorldInstancesSection();
            ImGui.Separator();
            DrawWindowsSection();

            ImGui.End();
        }

        private static void DrawPlayModeSection()
        {
            var state = Engine.PlayMode.State;
            
            // Color-code the state
            Vector4 stateColor = state switch
            {
                EPlayModeState.Edit => new Vector4(0.4f, 0.7f, 1.0f, 1.0f),      // Blue for edit
                EPlayModeState.Play => new Vector4(0.3f, 1.0f, 0.3f, 1.0f),      // Green for play
                EPlayModeState.Paused => new Vector4(1.0f, 1.0f, 0.3f, 1.0f),    // Yellow for paused
                EPlayModeState.EnteringPlay => new Vector4(0.5f, 1.0f, 0.5f, 1.0f),
                EPlayModeState.ExitingPlay => new Vector4(1.0f, 0.5f, 0.5f, 1.0f),
                _ => new Vector4(1.0f, 1.0f, 1.0f, 1.0f)
            };

            if (ImGui.CollapsingHeader("Play Mode", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent();
                
                ImGui.Text("State:");
                ImGui.SameLine();
                ImGui.TextColored(stateColor, state.ToString());

                // Play mode controls
                ImGui.Spacing();
                bool isPlaying = Engine.PlayMode.IsPlaying;
                bool isPaused = Engine.PlayMode.IsPaused;
                bool isEditing = Engine.PlayMode.IsEditing;
                bool isTransitioning = Engine.PlayMode.IsTransitioning;

                ImGui.BeginDisabled(isTransitioning);
                
                if (isEditing)
                {
                    if (ImGui.Button("▶ Play", new Vector2(80, 0)))
                        _ = Engine.PlayMode.EnterPlayModeAsync();
                }
                else if (isPlaying)
                {
                    if (ImGui.Button("⏸ Pause", new Vector2(80, 0)))
                        Engine.PlayMode.Pause();
                    ImGui.SameLine();
                    if (ImGui.Button("⏹ Stop", new Vector2(80, 0)))
                        _ = Engine.PlayMode.ExitPlayModeAsync();
                }
                else if (isPaused)
                {
                    if (ImGui.Button("▶ Resume", new Vector2(80, 0)))
                        Engine.PlayMode.Resume();
                    ImGui.SameLine();
                    if (ImGui.Button("⏹ Stop", new Vector2(80, 0)))
                        _ = Engine.PlayMode.ExitPlayModeAsync();
                    ImGui.SameLine();
                    if (ImGui.Button("⏭ Step", new Vector2(80, 0)))
                        Engine.PlayMode.StepFrame();
                }
                
                ImGui.EndDisabled();

                // Show active game mode
                ImGui.Spacing();
                var gameMode = Engine.PlayMode.ActiveGameMode;
                if (gameMode is not null)
                {
                    ImGui.Text("GameMode:");
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.8f, 0.8f, 1.0f, 1.0f), gameMode.GetType().Name);
                    
                    if (gameMode.WorldInstance is not null)
                    {
                        ImGui.Text("  World:");
                        ImGui.SameLine();
                        ImGui.Text(gameMode.WorldInstance.TargetWorld?.Name ?? "<unnamed>");
                    }
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No active GameMode");
                }

                ImGui.Unindent();
            }
        }

        private static void DrawPlayersSection()
        {
            if (ImGui.CollapsingHeader("Players", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent();

                var localPlayers = Engine.State.LocalPlayers;
                int activeCount = localPlayers.Count(p => p is not null);

                ImGui.Text($"Local Players: {activeCount}/4");
                ImGui.Spacing();

                if (activeCount == 0)
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No local players initialized");
                }
                else
                {
                    if (ImGui.BeginTable("PlayersTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                    {
                        ImGui.TableSetupColumn("Index", ImGuiTableColumnFlags.WidthFixed, 50);
                        ImGui.TableSetupColumn("Controller", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Pawn", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Viewport", ImGuiTableColumnFlags.WidthFixed, 70);
                        ImGui.TableHeadersRow();

                        for (int i = 0; i < localPlayers.Length; i++)
                        {
                            var player = localPlayers[i];
                            if (player is null) continue;

                            ImGui.TableNextRow();
                            
                            ImGui.TableNextColumn();
                            ImGui.Text($"P{i + 1}");

                            ImGui.TableNextColumn();
                            DrawControllerInfo(player);

                            ImGui.TableNextColumn();
                            DrawPawnInfo(player);

                            ImGui.TableNextColumn();
                            if (player.Viewport is not null)
                                ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f), "Yes");
                            else
                                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "None");
                        }

                        ImGui.EndTable();
                    }
                }

                // Remote players
                var remotePlayers = Engine.State.RemotePlayers;
                if (remotePlayers.Count > 0)
                {
                    ImGui.Spacing();
                    ImGui.Text($"Remote Players: {remotePlayers.Count}");
                    
                    if (ImGui.BeginTable("RemotePlayersTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                    {
                        ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 50);
                        ImGui.TableSetupColumn("Pawn", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableHeadersRow();

                        int idx = 0;
                        foreach (var remote in remotePlayers)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.Text($"R{idx++}");
                            ImGui.TableNextColumn();
                            ImGui.Text(remote.ControlledPawn?.GetType().Name ?? "<none>");
                        }

                        ImGui.EndTable();
                    }
                }

                ImGui.Unindent();
            }
        }

        private static void DrawControllerInfo(LocalPlayerController player)
        {
            var input = player.Input;
            if (input is null)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "<no input>");
                return;
            }

            // Show input devices using ASCII-compatible symbols
            string devices = "";
            if (input.Keyboard is not null)
                devices += "[KB] ";
            if (input.Mouse is not null)
                devices += "[M] ";
            if (input.Gamepad is not null)
                devices += "[GP] ";
            
            if (string.IsNullOrEmpty(devices))
                devices = "<no devices>";
            
            ImGui.Text(devices.Trim());
        }

        private static void DrawPawnInfo(LocalPlayerController player)
        {
            var pawn = player.ControlledPawn;
            if (pawn is null)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "<none>");
                return;
            }

            ImGui.TextColored(new Vector4(0.8f, 0.8f, 1.0f, 1.0f), pawn.GetType().Name);
            
            // Show pawn's node name if available
            if (pawn.SceneNode is not null)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), $"({pawn.SceneNode.Name})");
            }
        }

        private static void DrawWorldInstancesSection()
        {
            if (ImGui.CollapsingHeader("World Instances", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent();

                var worldInstances = XRWorldInstance.WorldInstances;
                ImGui.Text($"Active Instances: {worldInstances.Count}");
                ImGui.Spacing();

                if (worldInstances.Count == 0)
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No world instances active");
                }
                else
                {
                    if (ImGui.BeginTable("WorldsTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                    {
                        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Playing", ImGuiTableColumnFlags.WidthFixed, 55);
                        ImGui.TableSetupColumn("Physics", ImGuiTableColumnFlags.WidthFixed, 55);
                        ImGui.TableSetupColumn("GameMode", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableHeadersRow();

                        foreach (var kvp in worldInstances)
                        {
                            var world = kvp.Key;
                            var instance = kvp.Value;

                            ImGui.TableNextRow();
                            
                            ImGui.TableNextColumn();
                            ImGui.Text(world.Name ?? "<unnamed>");

                            ImGui.TableNextColumn();
                            if (instance.IsPlaying)
                                ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f), "Yes");
                            else
                                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No");

                            ImGui.TableNextColumn();
                            if (instance.PhysicsEnabled)
                                ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f), "On");
                            else
                                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Off");

                            ImGui.TableNextColumn();
                            ImGui.Text(instance.GameMode?.GetType().Name ?? "<none>");
                        }

                        ImGui.EndTable();
                    }

                    // Detailed view for each world
                    ImGui.Spacing();
                    foreach (var kvp in worldInstances)
                    {
                        var world = kvp.Key;
                        var instance = kvp.Value;
                        
                        if (ImGui.TreeNode($"{world.Name ?? "<unnamed>"} Details"))
                        {
                            ImGui.Text($"Root Nodes: {instance.RootNodes.Count}");
                            var lights = instance.Lights;
                            int lightCount = lights.DynamicSpotLights.Count + 
                                           lights.DynamicPointLights.Count + 
                                           lights.DynamicDirectionalLights.Count;
                            ImGui.Text($"Dynamic Lights: {lightCount}");
                            ImGui.Text($"Listeners: {instance.Listeners.Count}");

                            ImGui.TreePop();
                        }
                    }
                }

                ImGui.Unindent();
            }
        }

        private static void DrawWindowsSection()
        {
            if (ImGui.CollapsingHeader("Windows & Viewports", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent();

                var windows = Engine.Windows.ToArray();
                ImGui.Text($"Windows: {windows.Length}");
                ImGui.Spacing();

                if (windows.Length == 0)
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No windows active");
                }
                else
                {
                    foreach (var window in windows)
                    {
                        string windowTitle = window.Window?.Title ?? "<unnamed window>";
                        if (ImGui.TreeNode($"[W] {windowTitle}##Window{window.GetHashCode()}"))
                        {
                            var size = window.Window?.Size ?? new Silk.NET.Maths.Vector2D<int>(0, 0);
                            ImGui.Text($"Size: {size.X}x{size.Y}");
                            
                            // World instance
                            var worldInstance = window.TargetWorldInstance;
                            if (worldInstance is not null)
                            {
                                ImGui.Text("World:");
                                ImGui.SameLine();
                                ImGui.TextColored(new Vector4(0.8f, 0.8f, 1.0f, 1.0f), 
                                    worldInstance.TargetWorld?.Name ?? "<unnamed>");
                            }
                            else
                            {
                                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No world instance");
                            }

                            // Viewports
                            var viewports = window.Viewports;
                            if (viewports.Count > 0)
                            {
                                ImGui.Text($"Viewports: {viewports.Count}");
                                
                                if (ImGui.BeginTable($"ViewportsTable{window.GetHashCode()}", 3, 
                                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                                {
                                    ImGui.TableSetupColumn("Index", ImGuiTableColumnFlags.WidthFixed, 40);
                                    ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 100);
                                    ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
                                    ImGui.TableHeadersRow();

                                    int viewportIdx = 0;
                                    foreach (var viewport in viewports)
                                    {
                                        ImGui.TableNextRow();
                                        
                                        ImGui.TableNextColumn();
                                        ImGui.Text($"{viewportIdx++}");

                                        ImGui.TableNextColumn();
                                        ImGui.Text($"{viewport.Width}x{viewport.Height}");

                                        ImGui.TableNextColumn();
                                        var player = viewport.AssociatedPlayer;
                                        if (player is not null)
                                        {
                                            ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f), 
                                                $"P{(int)player.LocalPlayerIndex + 1}");
                                        }
                                        else
                                        {
                                            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "<none>");
                                        }
                                    }

                                    ImGui.EndTable();
                                }
                            }
                            else
                            {
                                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No viewports");
                            }

                            ImGui.TreePop();
                        }
                    }
                }

                ImGui.Unindent();
            }
        }
    }
}
