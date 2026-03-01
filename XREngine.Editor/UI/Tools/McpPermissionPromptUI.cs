using ImGuiNET;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Editor.Mcp;

namespace XREngine.Editor.UI.Tools;

/// <summary>
/// Renders an ImGui modal popup when an MCP tool call requires user permission.
/// Call <see cref="Render"/> every frame from the main ImGui render loop.
/// </summary>
public static class McpPermissionPromptUI
{
    private const string ModalId = "MCP Permission Request###McpPermModal";

    private static readonly Vector4 ColorReadOnly = new(0.40f, 0.80f, 0.40f, 1.00f);
    private static readonly Vector4 ColorMutate = new(1.00f, 0.85f, 0.20f, 1.00f);
    private static readonly Vector4 ColorDestructive = new(1.00f, 0.45f, 0.25f, 1.00f);
    private static readonly Vector4 ColorArbitrary = new(1.00f, 0.25f, 0.25f, 1.00f);
    private static readonly Vector4 ColorMuted = new(0.55f, 0.55f, 0.60f, 1.00f);
    private static readonly Vector4 ColorToolName = new(0.55f, 0.75f, 1.00f, 1.00f);

    private static bool _rememberChoice;
    private static bool _modalOpenedThisFrame;

    /// <summary>
    /// Renders the permission prompt modal if there is a pending request.
    /// Must be called once per frame from the main render loop.
    /// </summary>
    public static void Render()
    {
        var manager = McpPermissionManager.Instance;
        var request = manager.DequeueNextRequest();

        if (request is null)
        {
            _modalOpenedThisFrame = false;
            return;
        }

        // Open the modal on the first frame we see a request.
        if (!_modalOpenedThisFrame)
        {
            ImGui.OpenPopup(ModalId);
            _modalOpenedThisFrame = true;
            _rememberChoice = false;
        }

        ImGui.SetNextWindowSize(new Vector2(520, 0), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(
            ImGui.GetMainViewport().GetCenter(),
            ImGuiCond.Appearing,
            new Vector2(0.5f, 0.5f));

        if (!ImGui.BeginPopupModal(ModalId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
            return;

        // Header with risk-level badge
        DrawRiskBadge(request.PermissionLevel);
        ImGui.SameLine();
        ImGui.TextUnformatted("MCP Tool Permission Request");
        ImGui.Separator();
        ImGui.Spacing();

        // Source
        ImGui.TextColored(ColorMuted, "Source:");
        ImGui.SameLine();
        ImGui.TextUnformatted(request.Source);

        // Tool name
        ImGui.TextColored(ColorMuted, "Tool:");
        ImGui.SameLine();
        ImGui.TextColored(ColorToolName, request.ToolName);

        // Description
        ImGui.TextColored(ColorMuted, "Action:");
        ImGui.SameLine();
        ImGui.TextWrapped(request.ToolDescription);

        // Permission reason (if provided)
        if (!string.IsNullOrWhiteSpace(request.PermissionReason))
        {
            ImGui.TextColored(ColorMuted, "Reason:");
            ImGui.SameLine();
            ImGui.TextWrapped(request.PermissionReason);
        }

        // Arguments
        if (!string.IsNullOrWhiteSpace(request.ArgumentsSummary))
        {
            ImGui.Spacing();
            ImGui.TextColored(ColorMuted, "Arguments:");
            ImGui.Indent(8f);

            // Show in a scrollable child if the summary is long
            string summary = request.ArgumentsSummary;
            if (summary.Length > 300)
            {
                ImGui.BeginChild("##args_scroll", new Vector2(-1, 80), ImGuiChildFlags.Border);
                ImGui.TextWrapped(summary);
                ImGui.EndChild();
            }
            else
            {
                ImGui.TextWrapped(summary);
            }
            ImGui.Unindent(8f);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Risk explanation
        DrawRiskExplanation(request.PermissionLevel);

        ImGui.Spacing();

        // Remember checkbox
        ImGui.Checkbox("Remember this choice for this tool (this session only)", ref _rememberChoice);
        request.RememberChoice = _rememberChoice;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Action buttons
        float buttonWidth = 120f;
        float totalWidth = buttonWidth * 2 + ImGui.GetStyle().ItemSpacing.X;
        float availWidth = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - totalWidth) * 0.5f);

        // Approve button — green-tinted
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.55f, 0.20f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.20f, 0.70f, 0.25f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.10f, 0.45f, 0.15f, 1.00f));
        if (ImGui.Button("Allow", new Vector2(buttonWidth, 0)))
        {
            manager.ApproveActiveRequest();
            _modalOpenedThisFrame = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleColor(3);

        ImGui.SameLine();

        // Deny button — red-tinted
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.60f, 0.15f, 0.15f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.20f, 0.20f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.50f, 0.10f, 0.10f, 1.00f));
        if (ImGui.Button("Deny", new Vector2(buttonWidth, 0)))
        {
            manager.DenyActiveRequest();
            _modalOpenedThisFrame = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleColor(3);

        ImGui.EndPopup();
    }

    private static void DrawRiskBadge(McpPermissionLevel level)
    {
        string label = level switch
        {
            McpPermissionLevel.ReadOnly => " READ ",
            McpPermissionLevel.Mutate => " MUTATE ",
            McpPermissionLevel.Destructive => " DESTRUCTIVE ",
            McpPermissionLevel.Arbitrary => " ARBITRARY ",
            _ => " UNKNOWN ",
        };

        Vector4 color = GetLevelColor(level);

        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0, 0, 0, 1));
        ImGui.SmallButton(label);
        ImGui.PopStyleColor(4);
    }

    private static void DrawRiskExplanation(McpPermissionLevel level)
    {
        string explanation = level switch
        {
            McpPermissionLevel.ReadOnly =>
                "This tool only reads data and has no side effects.",
            McpPermissionLevel.Mutate =>
                "This tool will modify scene state. Changes can be undone.",
            McpPermissionLevel.Destructive =>
                "This tool will DELETE data (scene nodes, files, etc.). This may not be fully undoable.",
            McpPermissionLevel.Arbitrary =>
                "This tool executes ARBITRARY operations (method invocation, code compilation, file writes). Verify the arguments carefully.",
            _ =>
                "Unknown risk level.",
        };

        Vector4 color = GetLevelColor(level);
        ImGui.TextColored(color, explanation);
    }

    private static Vector4 GetLevelColor(McpPermissionLevel level) => level switch
    {
        McpPermissionLevel.ReadOnly => ColorReadOnly,
        McpPermissionLevel.Mutate => ColorMutate,
        McpPermissionLevel.Destructive => ColorDestructive,
        McpPermissionLevel.Arbitrary => ColorArbitrary,
        _ => ColorMuted,
    };
}
