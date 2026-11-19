using ImGuiNET;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Text.Json;
using XREngine.Editor.Mcp;
using XREngine.Data.Core;

namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private InteractionMode _activeInteractionMode = InteractionMode.Explain;
    private string _activeUserInstructions = string.Empty;
    private readonly HashSet<string> _requestReadOnlyTools = new(StringComparer.Ordinal);
    private AssistantHelpRequest? _draftHelpRequest;
    private AssistantHelpRequest? _activeHelpRequest;
    private bool _refreshHelpDraft;
    private static readonly Regex ExplanationIntent = new(@"^\s*(what|why|how|explain|describe|tell me|show me how|can you explain|could you explain)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EditIntent = new(@"^\s*(please\s+)?((can|could|would) you\s+)?(change|set|create|add|remove|delete|rename|move|rotate|scale|enable|disable|fix|apply|replace|write|implement|build)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> InspectionTools = new(StringComparer.Ordinal)
    {
        "search_engine_help", "read_engine_help", "search_types", "get_type_info", "get_type_members",
        "get_method_info", "get_derived_types", "get_parent_types", "get_type_hierarchy_tree",
        "list_assemblies", "get_assembly_types", "list_enums", "get_enum_values", "list_component_types",
        "get_component_schema", "get_component_property", "get_component_snapshot", "list_components",
        "list_worlds", "list_scene_nodes", "get_scene_node_info", "find_nodes_by_name", "find_nodes_by_type",
        "get_selection", "get_engine_state", "get_time_state", "get_render_state", "get_scene_statistics",
        "get_node_world_transform", "list_transform_children", "get_transform_matrices", "get_transform_decomposed",
        "list_layers", "list_tags", "list_loaded_assets", "get_asset_info",
    };

    private bool IsReadOnlyRequest => _activeInteractionMode != InteractionMode.Edit;

    /// <summary>Captures user choices once so an active request cannot gain authority from later UI changes.</summary>
    private void CaptureRequestIntent(string prompt)
    {
        InteractionMode selected = (InteractionMode)(Prefs?.McpAssistantInteractionMode ?? 0);
        _activeInteractionMode = selected == InteractionMode.Auto
            ? ExplanationIntent.IsMatch(prompt) ? InteractionMode.Explain
            : EditIntent.IsMatch(prompt) ? InteractionMode.Edit : InteractionMode.Diagnose
            : selected;
        _activeUserInstructions = Prefs?.McpAssistantUserInstructions ?? string.Empty;
        _activeReasoningEffort = GetSelectedEffort();
        _activeHelpRequest = _draftHelpRequest;
        _draftHelpRequest = null;
        _requestReadOnlyTools.Clear();
    }

    /// <summary>Uses host tool metadata rather than a model claim to permit read-only calls.</summary>
    private bool IsReadOnlyAssistantTool(string name)
        => InspectionTools.Contains(name)
            && (McpToolRegistry.TryGetTool(name, out McpToolDefinition? tool)
                && tool?.PermissionLevel == McpPermissionLevel.ReadOnly);

    private bool CanInvokeAssistantTool(string name)
        => !IsReadOnlyRequest || _requestReadOnlyTools.Contains(name);

    private void SetMessageExplanationContext(ChatMessage message, string question)
    {
        message.IsExplanation = IsReadOnlyRequest;
        message.UserQuestion = question;
        message.HelpRequest = _activeHelpRequest;
    }

    /// <summary>Moves a clicked editor reference into an editable draft without sending it.</summary>
    private void TakePendingHelpRequest()
    {
        if (_isBusy || !AssistantHelpRequests.TryTake(out AssistantHelpRequest? request) || request is null)
            return;
        FillExplanationDraft($"Explain {request.Label}. Describe its visible effect, when it applies, and relevant tradeoffs.\n\nCaptured editor reference (inspect current state when needed):\n{JsonSerializer.Serialize(request)}", request);
        AttachMcpServer = true;
    }

    private void FillExplanationDraft(string text, AssistantHelpRequest? request)
    {
        _prompt = text.Length > 24_000 ? text[..24_000] : text;
        _draftHelpRequest = request;
        _pendingPromptClear = false;
        _refreshHelpDraft = true;
        if (Prefs is { } preferences)
            preferences.McpAssistantInteractionMode = (int)InteractionMode.Explain;
        SetStatus("Review the question, then send.", ColorReady);
    }

    /// <summary>Offers deeper explanation and navigation without changing the selected setting.</summary>
    private void DrawExplanationActions(ChatMessage message)
    {
        DrawSourceReferences(message);
        if (!message.IsExplanation || message.IsStreaming || string.IsNullOrWhiteSpace(message.Content))
            return;
        ImGui.BeginDisabled(_isBusy);
        if (ImGui.SmallButton("How it works"))
            FillExplanationDraft("Explain the mechanism, inputs, and outputs behind this question. Use source evidence and distinguish inference:\n" + message.UserQuestion, message.HelpRequest);
        if (ImGui.SmallButton("Show source"))
            FillExplanationDraft("Find and explain the relevant engine implementation and callers for this question. Cite inspected file paths and lines. State whether source matches the running build:\n" + message.UserQuestion, message.HelpRequest);
        if (message.HelpRequest is { TargetId: not null } help && ImGui.SmallButton("Show in editor"))
        {
            if (!AssistantHelpRequests.ShowTarget(help))
                SetStatus("That object is no longer available in this scene.", ColorError);
        }
        ImGui.EndDisabled();
    }

    private void DrawInteractionMode()
    {
        ImGui.BeginDisabled(_isBusy);
        int mode = Prefs?.McpAssistantInteractionMode ?? 0;
        ImGui.SetNextItemWidth(Math.Min(220f, ImGui.GetContentRegionAvail().X));
        if (ImGui.Combo("##AssistantIntent", ref mode, "Auto\0Explain (read only)\0Diagnose (read only)\0Edit\0") && Prefs is { } preferences)
            preferences.McpAssistantInteractionMode = mode;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Auto uses read-only tools for questions and uncertain requests. Select Edit to request changes explicitly.");
        ImGui.EndDisabled();
    }

    private void DrawPersonalInstructions()
    {
        if (Prefs is not { } preferences)
            return;
        ImGui.TextUnformatted("Personal instructions");
        ImGui.TextWrapped("Add your own system prompt. For example: use plain English, explain new terms, and keep answers short. Engine instructions and tool permissions still apply.");
        string instructions = preferences.McpAssistantUserInstructions;
        ImGui.BeginDisabled(_isBusy);
        if (ImGui.InputTextMultiline("##AssistantUserInstructions", ref instructions, 8193, new Vector2(-1f, 110f)))
            preferences.McpAssistantUserInstructions = instructions;
        if (ImGui.SmallButton("Clear personal instructions"))
            preferences.McpAssistantUserInstructions = string.Empty;
        ImGui.EndDisabled();
        ImGui.TextDisabled("Saved on this computer. Sent with each new request.");
    }
}
