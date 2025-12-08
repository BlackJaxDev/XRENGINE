using ImGuiNET;
using System;
using System.Linq;
using System.Numerics;
using XREngine;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;

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
                            if (instance.PlayState == XRWorldInstance.EPlayState.Playing)
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

                                // Light probe details and previews for each viewport
                                foreach (var viewport in viewports)
                                {
                                    var pipeline = viewport.RenderPipelineInstance?.Pipeline as DefaultRenderPipeline;
                                    if (pipeline is null)
                                        continue;

                                    var irr = pipeline.ProbeIrradianceArray;
                                    var pre = pipeline.ProbePrefilterArray;
                                    int probeCount = pipeline.ProbeCount;
                                    uint width = irr?.Width ?? pre?.Width ?? 0u;
                                    uint height = irr?.Height ?? pre?.Height ?? 0u;
                                    uint layers = irr?.Depth ?? pre?.Depth ?? 0u;

                                    bool hasArrays = (irr is not null || pre is not null) && layers > 0;
                                    if (!hasArrays)
                                        continue;

                                    ImGui.Spacing();
                                    ImGui.PushID(viewport.GetHashCode());
                                    if (ImGui.TreeNode($"Light Probes##{viewport.GetHashCode()}") )
                                    {
                                        ImGui.Text($"Probes Built: {probeCount}");
                                        ImGui.Text($"Array Size: {width} x {height}");
                                        ImGui.Text($"Layers: {layers}");

                                        int maxLayer = Math.Max(0, (int)layers - 1);
                                        if (_probePreviewLayer > maxLayer)
                                            _probePreviewLayer = maxLayer;
                                        ImGui.SliderInt("Preview Layer", ref _probePreviewLayer, 0, maxLayer);

                                        DrawProbeArrayPreview("Irradiance", irr, _probePreviewLayer);
                                        DrawProbeArrayPreview("Prefilter", pre, _probePreviewLayer);
                                        ImGui.TreePop();
                                    }
                                    ImGui.PopID();
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

        private static void DrawProbeArrayPreview(string label, XRTexture2DArray? array, int layerIndex)
        {
            if (array is null || array.Textures.Length == 0)
            {
                ImGui.TextDisabled($"{label}: not available");
                return;
            }

            int clampedLayer = Math.Clamp(layerIndex, 0, array.Textures.Length - 1);
            XRTexture2D? layerTexture = array.Textures[clampedLayer];
            if (layerTexture is null)
            {
                ImGui.TextDisabled($"{label}: no layer texture");
                return;
            }

            if (!TryGetTexturePreviewData(layerTexture, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string? failure))
            {
                ImGui.TextDisabled(failure ?? $"{label}: preview unavailable");
                return;
            }

            Vector2 uv0 = new(0.0f, 1.0f);
            Vector2 uv1 = new(1.0f, 0.0f);
            ImGui.TextUnformatted($"{label} (Layer {clampedLayer})");
            ImGui.Image(handle, displaySize, uv0, uv1);
            ImGui.TextDisabled($"{(int)pixelSize.X} x {(int)pixelSize.Y}");
        }

        private static bool TryGetTexturePreviewData(
            XRTexture texture,
            out nint handle,
            out Vector2 displaySize,
            out Vector2 pixelSize,
            out string? failureReason)
        {
            pixelSize = GetTexturePixelSize(texture);
            displaySize = GetPreviewSize(pixelSize);
            handle = nint.Zero;
            failureReason = null;

            if (!Engine.IsRenderThread)
            {
                failureReason = "Preview unavailable off render thread";
                return false;
            }

            var renderer = TryGetOpenGLRenderer();
            if (renderer is null)
            {
                failureReason = "Preview requires OpenGL renderer";
                return false;
            }

            switch (texture)
            {
                case XRTexture2D tex2D:
                    var apiTexture = renderer.GenericToAPI<GLTexture2D>(tex2D);
                    if (apiTexture is null)
                    {
                        failureReason = "Texture not uploaded";
                        return false;
                    }

                    uint binding = apiTexture.BindingId;
                    if (binding == OpenGLRenderer.GLObjectBase.InvalidBindingId || binding == 0)
                    {
                        failureReason = "Texture not ready";
                        return false;
                    }

                    handle = (nint)binding;
                    return true;

                default:
                    failureReason = $"{texture.GetType().Name} preview not supported";
                    return false;
            }
        }

        private static Vector2 GetTexturePixelSize(XRTexture texture)
        {
            return texture switch
            {
                XRTexture2D tex2D => new Vector2(tex2D.Width, tex2D.Height),
                _ => new Vector2(texture.WidthHeightDepth.X, texture.WidthHeightDepth.Y),
            };
        }

        private static Vector2 GetPreviewSize(Vector2 pixelSize)
        {
            const float maxPreviewWidth = 256f;
            const float maxPreviewHeight = 256f;

            float aspect = pixelSize.X > 0f ? pixelSize.Y / pixelSize.X : 1f;
            float width = MathF.Min(maxPreviewWidth, pixelSize.X);
            float height = width * aspect;

            if (height > maxPreviewHeight && aspect > 0f)
            {
                height = maxPreviewHeight;
                width = height / aspect;
            }

            return new Vector2(width, height);
        }
    }
}
