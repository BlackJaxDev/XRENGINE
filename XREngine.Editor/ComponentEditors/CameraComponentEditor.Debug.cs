using ImGuiNET;
using System.Numerics;
using XREngine.Components;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;

namespace XREngine.Editor.ComponentEditors;

public sealed partial class CameraComponentEditor
{
    /// <summary>
    /// Collects camera and pipeline diagnostics without changing the ownership of their settings.
    /// Also supports runtime eye cameras without a CameraComponent.
    /// </summary>
    private static void DrawRuntimeCameraDebug(PipelineEditorContext context)
    {
        using var profilerScope = Engine.Profiler.Start("UI.ComponentEditor.CameraComponent.Debug");
        XRCamera camera = context.Camera;
        CameraComponent? component = context.Component;
        RenderPipeline selectedPipeline = context.SelectedPipeline;
        ImGui.PushID("CameraDebugPanel");
        var schema = context.Schema;
        var state = context.State;
        ImGui.BeginDisabled(IsPipelineTypePreview(selectedPipeline));
        selectedPipeline.EditorUIProvider?.DrawDebug(context);

        if (state is not null)
        {
            foreach (PostProcessStageEntry stage in BuildOrderedStageEntries(schema, state, PipelineEditorSection.Debug))
            {
                ImGui.PushID(stage.Descriptor.Key);
                if (ImGui.CollapsingHeader(stage.Descriptor.DisplayName, ImGuiTreeNodeFlags.DefaultOpen))
                    DrawSchemaStageSelection(stage, camera, component, PipelineEditorSection.Debug, context.IsActivePipeline);
                ImGui.PopID();
            }
        }

        ImGui.EndDisabled();

        if (ImGui.CollapsingHeader("Camera Status & Bindings"))
        {
            if (component is not null)
                DrawUsageStatus(component);
            else
            {
                ImGui.TextDisabled($"Camera: {camera.GetHashCode()}");
                ImGui.TextDisabled($"Viewports: {camera.Viewports.Count}");
                ImGui.TextWrapped("Runtime camera: no scene camera component.");
            }
        }
        ImGui.PopID();
    }

    private static void DrawUsageStatus(CameraComponent component)
    {
        bool isActive = component.IsActivelyRendering;
        var player = component.GetUsingLocalPlayer();
        var pawn = component.GetUsingPawn();

        // Authoritative viewport bindings: scan live windows.
        var boundViewports = BoundViewportScratch;
        boundViewports.Clear();
        foreach (var vp in RuntimeEngine.EnumerateActiveViewports())
        {
            if (ReferenceEquals(vp.CameraComponent, component))
                boundViewports.Add(vp);
        }

        // Status indicator with color
        Vector4 statusColor = isActive 
            ? new Vector4(0.2f, 0.8f, 0.2f, 1.0f)  // Green for active
            : new Vector4(0.6f, 0.6f, 0.6f, 1.0f); // Gray for inactive
        
        string statusIcon = isActive ? "[ACTIVE]" : "[INACTIVE]";
        ImGui.TextColored(statusColor, statusIcon);
        ImGui.SameLine();
        ImGui.Text("Rendering Status");

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(component.GetUsageDescription());

        ImGui.Indent();

        // Local Player info
        if (player is not null)
        {
            ELocalPlayerIndex? localPlayerIndex = player.LocalPlayerIndex;
            if (localPlayerIndex.HasValue)
            {
                int playerNumber = (int)localPlayerIndex.GetValueOrDefault() + 1;
                ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), $"Local Player: {playerNumber}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"This camera is being used by Local Player {playerNumber}'s viewport.");
            }
            else
            {
                ImGui.TextDisabled("Local Player: None");
            }
        }
        else
        {
            ImGui.TextDisabled("Local Player: None");
        }

        // Pawn info
        if (pawn is not null)
        {
            string pawnName = pawn.SceneNode?.Name ?? pawn.GetType().Name;
            string pawnType = pawn.GetType().Name;
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.6f, 1.0f), $"Pawn: {pawnName}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Type: {pawnType}\nThis camera is provided by this pawn component.");
        }
        else
        {
            ImGui.TextDisabled("Pawn: None");
        }

        // Viewport count
        var cam = component.Camera;
        int viewportCount = boundViewports.Count;
        ImGui.TextDisabled($"(CameraHash: {cam.GetHashCode()})");
        ImGui.TextDisabled($"(XRCamera.Viewports: {cam.Viewports.Count})");
        if (viewportCount > 0)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.4f, 1.0f), $"Viewports: {viewportCount}");
            if (ImGui.IsItemHovered())
            {
                string tooltip = "Bound viewports:\n";
                for (int i = 0; i < boundViewports.Count; i++)
                {
                    var vp = boundViewports[i];
                    tooltip += $"  [{i}] {vp.Region.Width}x{vp.Region.Height}";
                    if (vp.Window is not null)
                        tooltip += $" (Window: {vp.Window.WindowTitle})";
                    tooltip += "\n";
                }
                ImGui.SetTooltip(tooltip.TrimEnd());
            }
        }
        else
        {
            ImGui.TextDisabled("Viewports: 0");
        }

        // Warning if not in use
        if (!isActive)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.2f, 1.0f), "⚠ Camera is not rendering");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("This camera is not bound to any viewport or render target.\nIt will not consume rendering resources.");
        }

        ImGui.Unindent();
    }

}
