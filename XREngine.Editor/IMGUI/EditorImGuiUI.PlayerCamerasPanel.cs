using ImGuiNET;
using System.Collections.Generic;
using System.Numerics;
using XREngine;
using XREngine.Components;
using XREngine.Editor.ComponentEditors;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static readonly List<PlayerCameraPanelEntry> PlayerCameraPanelEntries = [];
    private static readonly HashSet<XRCamera> PlayerCameraPanelSeenCameras = [];
    private static readonly HashSet<object> PlayerCameraPanelVisited = [];
    private static readonly CameraComponentEditor PlayerCameraPanelCameraEditor = new();

    private readonly record struct PlayerCameraPanelEntry(
        string Label,
        string Source,
        XRCamera Camera,
        CameraComponent? Component,
        XRViewport? Viewport,
        XRRenderPipelineInstance? PipelineInstance,
        RenderPipeline? Pipeline,
        IPawnController? Player,
        SceneNode? SceneNode,
        bool RuntimeOwnedProjection,
        string? SharedSettingsLabel);

    private static void DrawPlayerCamerasPanel()
    {
        if (!_showPlayerCameras)
            return;

        if (!ImGui.Begin("Player Cameras", ref _showPlayerCameras, ImGuiWindowFlags.MenuBar))
        {
            ImGui.End();
            return;
        }

        BuildPlayerCameraPanelEntries();

        if (ImGui.BeginMenuBar())
        {
            ImGui.TextDisabled($"{PlayerCameraPanelEntries.Count} camera(s)");
            ImGui.EndMenuBar();
        }

        if (PlayerCameraPanelEntries.Count == 0)
        {
            ImGui.TextDisabled("No active player, viewport, or XR eye cameras are available.");
            ImGui.End();
            return;
        }

        for (int i = 0; i < PlayerCameraPanelEntries.Count; i++)
        {
            PlayerCameraPanelEntry entry = PlayerCameraPanelEntries[i];
            ImGui.PushID($"PlayerCameraEntry{i}_{entry.Camera.GetHashCode()}");

            ImGuiTreeNodeFlags flags = PlayerCameraPanelEntries.Count == 1
                ? ImGuiTreeNodeFlags.DefaultOpen
                : ImGuiTreeNodeFlags.None;

            string pipelineName = entry.Pipeline?.DebugName ?? entry.PipelineInstance?.DebugName ?? "No Pipeline";
            if (ImGui.CollapsingHeader($"{entry.Label} [{pipelineName}]", flags))
            {
                DrawPlayerCameraPanelEntry(entry);
                ImGui.Spacing();
            }

            ImGui.PopID();
        }

        ImGui.End();
    }

    private static void BuildPlayerCameraPanelEntries()
    {
        PlayerCameraPanelEntries.Clear();
        PlayerCameraPanelSeenCameras.Clear();

        foreach (XRViewport viewport in Engine.EnumerateActiveViewports())
            AddViewportCameraEntry(viewport, BuildViewportCameraLabel(viewport), "Active Viewport");

        for (int playerIndex = 0; playerIndex < Engine.State.LocalPlayers.Length; playerIndex++)
        {
            IPawnController? player = Engine.State.LocalPlayers[playerIndex];
            if (player is null)
                continue;

            XRViewport? viewport = player.Viewport as XRViewport;
            CameraComponent? component = viewport?.CameraComponent
                ?? (player.ControlledPawnComponent as IRuntimeInputControllablePawn)?.RuntimeCameraComponent as CameraComponent;

            if (component is null)
                continue;

            string label = BuildPlayerCameraLabel(player, component);
            AddCameraEntry(
                label,
                "Local Player",
                component.Camera,
                component,
                viewport,
                viewport?.RenderPipelineInstance,
                viewport?.RenderPipeline,
                player,
                component.SceneNode,
                runtimeOwnedProjection: false);
        }

        var vrInfo = Engine.VRState.ViewInformation;
        AddVrEyeCameraEntry("XR Left Eye", vrInfo.LeftEyeCamera, Engine.VRState.LeftEyeViewport, vrInfo.HMDNode);
        AddVrEyeCameraEntry("XR Right Eye", vrInfo.RightEyeCamera, Engine.VRState.RightEyeViewport, vrInfo.HMDNode);

        foreach (XRViewport viewport in Engine.EnumerateActiveViewports(Engine.EViewportEnumerationMode.IncludeVrEyeViewports))
            AddViewportCameraEntry(viewport, BuildViewportCameraLabel(viewport), "Active Viewport");
    }

    private static void AddViewportCameraEntry(XRViewport viewport, string label, string source)
    {
        XRCamera? camera = viewport.ActiveCamera;
        if (camera is null)
            return;

        CameraComponent? component = viewport.CameraComponent;
        AddCameraEntry(
            label,
            source,
            camera,
            component,
            viewport,
            viewport.RenderPipelineInstance,
            viewport.RenderPipeline,
            viewport.AssociatedPlayer,
            component?.SceneNode ?? camera.Transform.SceneNode,
            runtimeOwnedProjection: false);
    }

    private static void AddVrEyeCameraEntry(string label, XRCamera? camera, XRViewport? viewport, SceneNode? hmdNode)
    {
        if (camera is null)
            return;

        RenderPipeline? pipeline = viewport?.RenderPipeline ?? camera.RenderPipeline;
        XRRenderPipelineInstance? instance = ResolvePipelineInstanceForCamera(camera, pipeline, viewport);

        AddCameraEntry(
            label,
            "XR Runtime",
            camera,
            null,
            viewport,
            instance,
            pipeline,
            viewport?.AssociatedPlayer,
            hmdNode ?? camera.Transform.SceneNode,
            runtimeOwnedProjection: true,
            sharedSettingsLabel: "OpenXR stereo eye pair");
    }

    private static void AddCameraEntry(
        string label,
        string source,
        XRCamera camera,
        CameraComponent? component,
        XRViewport? viewport,
        XRRenderPipelineInstance? pipelineInstance,
        RenderPipeline? pipeline,
        IPawnController? player,
        SceneNode? sceneNode,
        bool runtimeOwnedProjection,
        string? sharedSettingsLabel = null)
    {
        if (!PlayerCameraPanelSeenCameras.Add(camera))
            return;

        pipeline ??= pipelineInstance?.Pipeline ?? camera.RenderPipeline;
        pipelineInstance ??= ResolvePipelineInstanceForCamera(camera, pipeline, viewport);

        PlayerCameraPanelEntries.Add(new PlayerCameraPanelEntry(
            label,
            source,
            camera,
            component,
            viewport,
            pipelineInstance,
            pipeline,
            player,
            sceneNode ?? component?.SceneNode ?? camera.Transform.SceneNode,
            runtimeOwnedProjection,
            sharedSettingsLabel));
    }

    private static XRRenderPipelineInstance? ResolvePipelineInstanceForCamera(XRCamera camera, RenderPipeline? pipeline, XRViewport? viewport)
    {
        if (viewport is not null && ReferenceEquals(viewport.ActiveCamera, camera))
            return viewport.RenderPipelineInstance;

        if (pipeline is null)
            return null;

        for (int i = 0; i < pipeline.Instances.Count; i++)
        {
            XRRenderPipelineInstance instance = pipeline.Instances[i];
            if (ReferenceEquals(instance.LastSceneCamera, camera) ||
                ReferenceEquals(instance.LastRenderingCamera, camera))
            {
                return instance;
            }
        }

        var vrInfo = Engine.VRState.ViewInformation;
        if (ReferenceEquals(camera, vrInfo.RightEyeCamera) && vrInfo.LeftEyeCamera is not null)
        {
            for (int i = 0; i < pipeline.Instances.Count; i++)
            {
                XRRenderPipelineInstance instance = pipeline.Instances[i];
                if (ReferenceEquals(instance.LastSceneCamera, vrInfo.LeftEyeCamera))
                    return instance;
            }
        }

        return pipeline.Instances.Count == 1 ? pipeline.Instances[0] : null;
    }

    private static void DrawPlayerCameraPanelEntry(PlayerCameraPanelEntry entry)
    {
        DrawPlayerCameraSummary(entry);

        if (entry.RuntimeOwnedProjection)
        {
            string sourceText = entry.SharedSettingsLabel is not null
                ? $"Pose and projection are runtime-updated. AA and post-processing are shared with {entry.SharedSettingsLabel}."
                : "Pose and projection are runtime-updated each frame.";
            ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.2f, 1.0f), sourceText);
        }

        PlayerCameraPanelVisited.Clear();
        if (!ImGui.BeginTabBar("PlayerCameraTabs"))
            return;

        if (ImGui.BeginTabItem(entry.Component is null ? "Camera" : "Component"))
        {
            if (entry.Component is not null)
                PlayerCameraPanelCameraEditor.DrawInspector(entry.Component, PlayerCameraPanelVisited);
            else
                DrawRuntimeObjectInspector("Camera", entry.Camera, PlayerCameraPanelVisited, defaultOpen: true);

            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Projection"))
        {
            CameraComponentEditor.DrawRuntimeCameraProjection(entry.Camera, PlayerCameraPanelVisited);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Post Processing"))
        {
            CameraComponentEditor.DrawRuntimeCameraPostProcessing(entry.Camera, entry.Pipeline, entry.Component, PlayerCameraPanelVisited);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Pipeline"))
        {
            DrawPlayerCameraPipelineControls(entry);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Viewport"))
        {
            DrawPlayerCameraViewportControls(entry);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private static void DrawPlayerCameraSummary(PlayerCameraPanelEntry entry)
    {
        if (ImGui.BeginTable("PlayerCameraSummary", 2, ImGuiTableFlags.SizingStretchProp))
        {
            DrawPlayerCameraSummaryRow("Source", entry.Source);
            DrawPlayerCameraSummaryRow("Camera", DescribePlayerCamera(entry));
            DrawPlayerCameraSummaryRow("Player", DescribePlayer(entry.Player));
            DrawPlayerCameraSummaryRow("Viewport", DescribeViewport(entry.Viewport));
            DrawPlayerCameraSummaryRow("Pipeline", entry.Pipeline?.DebugName ?? entry.PipelineInstance?.DebugName ?? "<none>");
            DrawPlayerCameraSummaryRow("Instance", entry.PipelineInstance?.ProfilerKey ?? "<none>");
            if (entry.SharedSettingsLabel is not null)
                DrawPlayerCameraSummaryRow("Shared Settings", entry.SharedSettingsLabel);
            ImGui.EndTable();
        }

        bool drewAction = false;
        if (entry.SceneNode is not null)
        {
            if (ImGui.SmallButton("Select Node"))
                Selection.SceneNode = entry.SceneNode;
            drewAction = true;
        }

        if (entry.Pipeline is not null)
        {
            if (drewAction)
                ImGui.SameLine();

            if (ImGui.SmallButton("Open Pipeline Graph"))
                OpenRenderPipelineGraph(entry.Pipeline);
        }

        ImGui.Separator();
    }

    private static void DrawPlayerCameraSummaryRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }

    private static void DrawPlayerCameraPipelineControls(PlayerCameraPanelEntry entry)
    {
        if (entry.Pipeline is not null)
        {
            if (ImGui.SmallButton("Open Pipeline Graph"))
                OpenRenderPipelineGraph(entry.Pipeline);

            DrawRuntimeObjectInspector("Render Pipeline", entry.Pipeline, PlayerCameraPanelVisited, defaultOpen: false);
        }
        else
        {
            ImGui.TextDisabled("No render pipeline is available for this camera.");
        }

        ImGui.Separator();

        if (entry.PipelineInstance is not null)
            DrawRuntimeObjectInspector("Render Pipeline Instance", entry.PipelineInstance, PlayerCameraPanelVisited, defaultOpen: true);
        else
            ImGui.TextDisabled("No live render pipeline instance has rendered this camera yet.");
    }

    private static void DrawPlayerCameraViewportControls(PlayerCameraPanelEntry entry)
    {
        if (entry.Viewport is null)
        {
            ImGui.TextDisabled("This camera is not currently bound to an active viewport.");
            return;
        }

        DrawRuntimeObjectInspector("Viewport", entry.Viewport, PlayerCameraPanelVisited, defaultOpen: true);
    }

    private static string BuildViewportCameraLabel(XRViewport viewport)
    {
        if (viewport.AssociatedPlayer?.LocalPlayerIndex is ELocalPlayerIndex playerIndex)
            return $"Player {(int)playerIndex + 1} Viewport {viewport.Index}";

        string? windowTitle = viewport.Window?.Window?.Title;
        if (!string.IsNullOrWhiteSpace(windowTitle))
            return $"{windowTitle} Viewport {viewport.Index}";

        return $"Viewport {viewport.Index}";
    }

    private static string BuildPlayerCameraLabel(IPawnController player, CameraComponent component)
    {
        if (player.LocalPlayerIndex is ELocalPlayerIndex playerIndex)
            return $"Player {(int)playerIndex + 1} Camera";

        return component.SceneNode?.Name ?? component.Name ?? "Player Camera";
    }

    private static string DescribePlayerCamera(PlayerCameraPanelEntry entry)
        => entry.Component?.SceneNode?.Name
            ?? entry.Camera.Transform.SceneNode?.Name
            ?? entry.Component?.Name
            ?? $"Camera {entry.Camera.GetHashCode()}";

    private static string DescribePlayer(IPawnController? player)
    {
        if (player is null)
            return "<none>";

        if (player.LocalPlayerIndex is ELocalPlayerIndex playerIndex)
            return $"Player {(int)playerIndex + 1}";

        return player.GetType().Name;
    }

    private static string DescribeViewport(XRViewport? viewport)
    {
        if (viewport is null)
            return "<none>";

        string prefix = viewport.RendersToExternalSwapchainTarget ? "External" : "Window";
        return $"{prefix} VP[{viewport.Index}] {viewport.Width}x{viewport.Height} internal {viewport.InternalWidth}x{viewport.InternalHeight}";
    }
}
