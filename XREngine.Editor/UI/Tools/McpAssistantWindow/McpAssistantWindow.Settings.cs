using ImGuiNET;
using System.Numerics;

namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private bool _showAdvanced;
    private static readonly string[] ResponseLimitLabels = ["Short", "Standard", "Long"];
    private static readonly int[] ResponseLimits = [1024, 4096, 16384];

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMenuBar())
            return;
        if (ImGui.MenuItem("+ New chat", null, false, !_isBusy))
            StartNewConversation();
        if (ImGui.BeginMenu("History"))
        {
            if (ImGui.MenuItem("Copy last reply", null, false, _history.Count > 0))
                CopyLastResponse();
            if (ImGui.MenuItem("Copy conversation", null, false, _history.Count > 0))
                CopyFullHistory();
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem("Context"))
            ImGui.OpenPopup("Assistant context");
        float statusWidth = ImGui.CalcTextSize(_status).X + 12f;
        if (ImGui.GetContentRegionAvail().X > statusWidth)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - statusWidth);
            ImGui.TextColored(_statusColor, _status);
        }
        DrawConversationContextPopup();
        ImGui.EndMenuBar();
    }

    /// <summary>Clears the visible conversation and its retained context for a new chat.</summary>
    private void StartNewConversation()
    {
        _history.Clear();
        _draftHelpRequest = null;
        _activeHelpRequest = null;
        _chatRowHeightCache.Clear();
        _chatViews.Clear();
        _artifactFileNames.Clear();
        _followChat = true;
        _lastChatHistoryCount = 0;
        _conversationSummary = string.Empty;
        _historyCompactedThroughExclusive = 0;
        _lastPromptForContinue = string.Empty;
        _canContinueAfterRepromptLimit = false;
        ResetSubscriptionHistoryBoundary();
        SetStatus("Ready", ColorReady);
    }

    private void DrawSettingsSection()
    {
        UpdateModelCatalog();
        bool disableSelection = _isBusy;
        ImGui.BeginDisabled(disableSelection);
        if (ImGui.BeginTable("##AssistantConnection", 3, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Assistant", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Model", ImGuiTableColumnFlags.WidthStretch, 1.7f);
            ImGui.TableSetupColumn("Effort", ImGuiTableColumnFlags.WidthStretch, 0.9f);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted("Assistant");
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("##AssistantBrand", GetBrandLabel()))
            {
                for (int i = 0; i < BrandProviders.Length; i++)
                {
                    if (ImGui.Selectable(ProviderLabels[i], ProviderIndex == (int)BrandProviders[i]))
                    {
                        if (ProviderIndex != (int)BrandProviders[i])
                            StartNewConversation();
                        ProviderIndex = (int)BrandProviders[i];
                        UpdateModelCatalog();
                    }
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Changing assistant starts a new chat.");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted("Model");
            ImGui.SetNextItemWidth(-1f);
            if (UsesSubscription)
                SubscriptionEditorConnection.DrawModelPicker(SubscriptionBrand, "##AssistantModel");
            else
                DrawApiModelPicker();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted("Effort");
            ImGui.SetNextItemWidth(-1f);
            DrawEffortPicker();
            ImGui.EndTable();
        }
        ImGui.EndDisabled();

        if (ImGui.SmallButton(CanSendWithSelectedModel() ? "Manage" : "Connect / Manage"))
            ImGui.OpenPopup("Assistant connection");
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, CanSendWithSelectedModel() ? ColorReady : ColorTimestamp);
        ImGui.TextWrapped(GetConnectionStatus());
        ImGui.PopStyleColor();
        DrawConnectionPopup();

        ImGui.SetNextItemOpen(_showAdvanced, ImGuiCond.Always);
        _showAdvanced = ImGui.CollapsingHeader("Advanced");
        if (_showAdvanced)
        {
            // Scroll advanced controls separately so the composer remains reachable.
            float height = Math.Clamp(ImGui.GetContentRegionAvail().Y * 0.38f, 100f, 270f);
            ImGui.BeginChild("##AssistantAdvanced", new Vector2(-1f, height), ImGuiChildFlags.Borders);
            ImGui.BeginDisabled(disableSelection);
            DrawAdvancedSettings();
            ImGui.EndDisabled();
            ImGui.EndChild();
        }
        ImGui.Separator();
    }

    private void DrawConnectionPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(440f, 0f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("Assistant connection"))
            return;
        ImGui.BeginDisabled(_isBusy);
        DrawAccessMode();
        if (UsesSubscription)
        {
            ImGui.TextWrapped("Use your account in the installed client. Your plan's usage limits apply.");
            SubscriptionEditorConnection.DrawConnectionActions(SubscriptionBrand);
        }
        else if (IsApiBrand)
        {
            ImGui.TextWrapped("This connection uses an API key. API usage is billed separately from a chat subscription.");
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(GetApiCredential()) ? "No API key saved." : "API key saved.");
            if (ImGui.Button("Paste API key"))
            {
                string value = ImGui.GetClipboardText() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                    SetApiCredential(value.Trim());
            }
            ImGui.SameLine();
            if (ImGui.Button("Use environment key"))
            {
                string? value = Environment.GetEnvironmentVariable(GetApiKeyEnvironmentName());
                if (!string.IsNullOrWhiteSpace(value))
                    SetApiCredential(string.Empty, GetApiKeyEnvironmentName());
                else
                    SetStatus("No environment key found.", ColorError);
            }
            ImGui.TextWrapped(GetApiKeyHelp());
            if (ImGui.Button("Check connection"))
                InvalidateApiCatalog();
            ImGui.SameLine();
            if (ImGui.Button("Remove saved key"))
                SetApiCredential(string.Empty);
        }
        else
            ImGui.TextWrapped(GetConnectionStatus());
        ImGui.EndDisabled();
        ImGui.EndPopup();
    }

    private void DrawAccessMode()
    {
        if (!IsApiBrand)
        {
            if (UsesSubscription)
                ImGui.TextUnformatted("Access: Subscription");
            return;
        }
        ImGui.TextUnformatted("Access");
        int mode = UsesSubscription ? 0 : 1;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.Combo("##AssistantAccess", ref mode, "Subscription\0API key\0") && Prefs is { } preferences)
        {
            StartNewConversation();
            if (ProviderIndex == (int)ProviderType.Codex)
                preferences.McpAssistantUseCodexAppServer = mode == 0;
            else if (ProviderIndex == (int)ProviderType.ClaudeCode)
                preferences.McpAssistantUseClaudeCli = mode == 0;
            else
                preferences.McpAssistantUseGeminiCli = mode == 0;
            UpdateModelCatalog();
        }
    }

    private void DrawAdvancedSettings()
    {
        DrawPersonalInstructions();
        ImGui.Separator();
        if (UsesSubscription)
            SubscriptionEditorConnection.DrawExecutableSettings(SubscriptionBrand);

        if (IsApiBrand && !UsesSubscription)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Response");
            int index = Array.IndexOf(ResponseLimits, MaxTokens);
            string preview = index >= 0 ? ResponseLimitLabels[index] : "Saved limit";
            ImGui.SetNextItemWidth(180f);
            if (ImGui.BeginCombo("Response limit", preview))
            {
                for (int i = 0; i < ResponseLimits.Length; i++)
                    if (ImGui.Selectable(ResponseLimitLabels[i], MaxTokens == ResponseLimits[i]))
                        MaxTokens = ResponseLimits[i];
                ImGui.EndCombo();
            }
            bool continueAutomatically = MaxAutoReprompts > 0;
            if (ImGui.Checkbox("Continue long answers automatically", ref continueAutomatically))
                MaxAutoReprompts = continueAutomatically ? 3 : 0;
            bool summarize = AutoSummarizeNearContextLimit;
            if (ImGui.Checkbox("Summarize older messages when needed", ref summarize))
                AutoSummarizeNearContextLimit = summarize;
            if (ProviderIndex == (int)ProviderType.Codex)
            {
                bool realtime = UseRealtimeWebSocket;
                if (ImGui.Checkbox("Use realtime connection", ref realtime))
                {
                    UseRealtimeWebSocket = realtime;
                    RefreshApiModelSelection();
                }
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Editor");
        bool tools = AttachMcpServer;
        if (ImGui.Checkbox("Allow editor tools", ref tools))
        {
            AttachMcpServer = tools;
            RefreshMcpEndpointFromPreferences();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Allows scene inspection and edits under the editor's existing permission rules.");
        bool screenshot = AttachViewportScreenshot;
        if (ImGui.Checkbox("Include a screenshot", ref screenshot))
            AttachViewportScreenshot = screenshot;
        bool camera = AutoCameraView;
        if (ImGui.Checkbox("Keep the camera on the result", ref camera))
            AutoCameraView = camera;
        bool scroll = AutoScroll;
        if (ImGui.Checkbox("Follow new replies", ref scroll))
            AutoScroll = scroll;

        ImGui.Separator();
        ImGui.TextUnformatted("Troubleshooting");
        if (ImGui.Button("Refresh models"))
        {
            if (UsesSubscription)
                _ = SubscriptionEditorConnection.ProbeAsync(SubscriptionBrand);
            else
                InvalidateApiCatalog();
        }
        ImGui.SameLine();
        if (ImGui.Button("Copy diagnostics"))
            ImGui.SetClipboardText($"Assistant: {GetBrandLabel()}\nModel: {GetSelectedModel()}\nConnection: {GetConnectionStatus()}\nRuntime: {(UsesSubscription ? SubscriptionEditorConnection.RuntimeVersion(SubscriptionBrand) : "API")}\nEditor tools: {AttachMcpServer}");
        bool logging = VerboseAiLogging;
        if (ImGui.Checkbox("Detailed request logging", ref logging))
            VerboseAiLogging = logging;
        if (logging)
            ImGui.TextWrapped("Detailed logs can contain prompts, replies, and tool results.");
        DrawMcpUsageHistorySection();
    }

    private void DrawMcpUsageHistorySection()
    {
        if (!ImGui.TreeNode("Request history"))
            return;
        if (_mcpUsageHistory.Count == 0)
            ImGui.TextUnformatted("No requests yet.");
        for (int i = _mcpUsageHistory.Count - 1; i >= 0; i--)
        {
            McpUsageEntry entry = _mcpUsageHistory[i];
            ImGui.TextUnformatted(entry.Provider);
            ImGui.SameLine();
            ImGui.TextUnformatted(entry.Result);
            ImGui.TextWrapped(entry.PromptPreview);
            if (!string.IsNullOrWhiteSpace(entry.Note))
                ImGui.TextWrapped(entry.Note);
            ImGui.Separator();
        }
        ImGui.TreePop();
    }
}
