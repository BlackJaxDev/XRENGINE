using ImGuiNET;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XREngine.Editor.Mcp;
using XREngine.Rendering.UI;

namespace XREngine.Editor.UI.Tools;

/// <summary>
/// ImGui tool window providing chat-style interaction with AI providers (OpenAI / Anthropic)
/// and optional MCP server attachment for in-editor scene manipulation.
/// Supports streaming HTTP (SSE) for both providers and OpenAI Realtime WebSocket.
/// </summary>
public sealed class McpAssistantWindow
{
    // ── Types ────────────────────────────────────────────────────────────

    private enum ProviderType
    {
        Codex,
        ClaudeCode
    }

    private sealed class ToolCallEntry
    {
        public required string ToolName { get; init; }
        public string ArgsSummary { get; init; } = string.Empty;
        public string ResultSummary { get; set; } = string.Empty;
        public bool IsError { get; set; }
        public bool IsComplete { get; set; }
    }

    private sealed class ChatMessage
    {
        public required string Role { get; init; }
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public bool IsStreaming { get; set; }
        public List<ToolCallEntry> ToolCalls { get; } = [];
    }

    private sealed class McpUsageEntry
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public string Provider { get; init; } = string.Empty;
        public string PromptPreview { get; init; } = string.Empty;
        public bool AttachRequested { get; init; }
        public bool McpPayloadIncluded { get; set; }
        public string ToolChoice { get; set; } = "none";
        public bool RequireToolUse { get; set; }
        public bool McpDiscoveryFailed { get; set; }
        public bool RetriedWithoutMcp { get; set; }
        public int McpEventCount { get; set; }
        public int ToolEventCount { get; set; }
        public string Result { get; set; } = "Pending";
        public string Note { get; set; } = string.Empty;
    }

    // ── Constants ────────────────────────────────────────────────────────

    private static readonly string[] ProviderLabels =
    [
        "Codex (OpenAI)",
        "Claude Code (Anthropic)"
    ];

    private static readonly Vector4 ColorUser = new(0.40f, 0.72f, 1.00f, 1.00f);
    private static readonly Vector4 ColorAssistant = new(0.78f, 0.56f, 1.00f, 1.00f);
    private static readonly Vector4 ColorTimestamp = new(0.50f, 0.50f, 0.55f, 1.00f);
    private static readonly Vector4 ColorReady = new(0.60f, 0.60f, 0.60f, 1.00f);
    private static readonly Vector4 ColorBusy = new(1.00f, 0.85f, 0.20f, 1.00f);
    private static readonly Vector4 ColorDone = new(0.30f, 1.00f, 0.30f, 1.00f);
    private static readonly Vector4 ColorError = new(1.00f, 0.30f, 0.30f, 1.00f);

    // Extended palette for Copilot-style chat rendering.
    private static readonly Vector4 ColorChatBg = new(0.12f, 0.12f, 0.15f, 1.00f);
    private static readonly Vector4 ColorUserBubble = new(0.17f, 0.19f, 0.24f, 1.00f);
    private static readonly Vector4 ColorAssistantBubble = new(0.14f, 0.14f, 0.18f, 1.00f);
    private static readonly Vector4 ColorCodeFg = new(0.85f, 0.65f, 0.40f, 1.00f);
    private static readonly Vector4 ColorBold = new(0.95f, 0.95f, 0.95f, 1.00f);
    private static readonly Vector4 ColorBullet = new(0.50f, 0.65f, 1.00f, 1.00f);
    private static readonly Vector4 ColorHeader = new(0.55f, 0.75f, 1.00f, 1.00f);
    private static readonly Vector4 ColorSeparator = new(0.25f, 0.25f, 0.30f, 0.60f);
    private static readonly Vector4 ColorToolSection = new(0.18f, 0.18f, 0.22f, 1.00f);
    private static readonly Vector4 ColorToolSectionHover = new(0.22f, 0.22f, 0.28f, 1.00f);
    private static readonly Vector4 ColorMuted = new(0.55f, 0.55f, 0.60f, 1.00f);
    private static readonly Vector4 ColorCodeBlockBg = new(0.08f, 0.08f, 0.10f, 1.00f);
    private static readonly Vector4 ColorPlaceholder = new(0.65f, 0.65f, 0.70f, 1.00f);

    // Use infinite timeout on the client; per-request cancellation tokens handle timeouts.
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    // ── Singleton ────────────────────────────────────────────────────────

    private static McpAssistantWindow? _instance;
    public static McpAssistantWindow Instance => _instance ??= new McpAssistantWindow();

    // ── State ────────────────────────────────────────────────────────────

    private bool _isOpen;

    // Convenience accessor — never null at runtime when the editor is active.
    private static EditorPreferences? Prefs => Engine.EditorPreferences;

    // Local mirrors for fields that EditorPreferences doesn't store.
    // MCP URL/token are derived from the MCP Server section of prefs each time the window opens.
    private string _mcpServerUrl = "http://localhost:5467/mcp/";
    private string _mcpAuthToken = string.Empty;

    // Chat (ephemeral — not worth persisting)
    private string _prompt = string.Empty;
    private bool _pendingPromptClear; // Set after send to force-clear the ImGui text buffer next frame.
    private readonly List<ChatMessage> _history = [];
    private bool _isBusy;
    private string _status = "Ready";
    private Vector4 _statusColor = ColorReady;
    private bool _scrollToBottom;
    private CancellationTokenSource? _cts;
    private readonly object _openAiModelLock = new();
    private string[] _openAiTextModels = [];
    private bool _openAiModelsLoading;
    private string _openAiModelsStatus = "Not loaded";
    private bool _openAiModelsAutoRequested;
    private readonly List<McpUsageEntry> _mcpUsageHistory = [];
    private const int MaxMcpUsageEntries = 80;

    // ── Pref-backed accessors ────────────────────────────────────────────
    // These read/write Engine.EditorPreferences so values are persisted.
    // When prefs are unavailable (null), they fall back to sensible defaults.

    private int ProviderIndex
    {
        get => Prefs?.McpAssistantProviderIndex ?? 0;
        set { if (Prefs is { } p) p.McpAssistantProviderIndex = value; }
    }

    private string OpenAiApiKey
    {
        get => Prefs?.McpAssistantOpenAiApiKey ?? string.Empty;
        set { if (Prefs is { } p) p.McpAssistantOpenAiApiKey = value; }
    }

    private string AnthropicApiKey
    {
        get => Prefs?.McpAssistantAnthropicApiKey ?? string.Empty;
        set { if (Prefs is { } p) p.McpAssistantAnthropicApiKey = value; }
    }

    private string OpenAiModel
    {
        get => Prefs?.McpAssistantOpenAiModel ?? "gpt-5-codex";
        set { if (Prefs is { } p) p.McpAssistantOpenAiModel = value; }
    }

    private string OpenAiRealtimeModel
    {
        get => Prefs?.McpAssistantOpenAiRealtimeModel ?? "gpt-4o-realtime-preview";
        set { if (Prefs is { } p) p.McpAssistantOpenAiRealtimeModel = value; }
    }

    private string AnthropicModel
    {
        get => Prefs?.McpAssistantAnthropicModel ?? "claude-sonnet-4-5";
        set { if (Prefs is { } p) p.McpAssistantAnthropicModel = value; }
    }

    private int MaxTokens
    {
        get => Prefs?.McpAssistantMaxTokens ?? 4096;
        set { if (Prefs is { } p) p.McpAssistantMaxTokens = value; }
    }

    private bool UseRealtimeWebSocket
    {
        get => Prefs?.McpAssistantUseRealtimeWebSocket ?? false;
        set { if (Prefs is { } p) p.McpAssistantUseRealtimeWebSocket = value; }
    }

    private bool AttachMcpServer
    {
        get => Prefs?.McpAssistantAttachMcpServer ?? true;
        set { if (Prefs is { } p) p.McpAssistantAttachMcpServer = value; }
    }

    private bool AutoScroll
    {
        get => Prefs?.McpAssistantAutoScroll ?? true;
        set { if (Prefs is { } p) p.McpAssistantAutoScroll = value; }
    }

    // ── Public API ───────────────────────────────────────────────────────

    public bool IsOpen
    {
        get => _isOpen;
        set => _isOpen = value;
    }

    public void Open()
    {
        _isOpen = true;
        RefreshMcpEndpointFromPreferences();
    }

    public void Close() => _isOpen = false;
    public void Toggle() => _isOpen = !_isOpen;

    // ── Render ───────────────────────────────────────────────────────────

    public void Render()
    {
        if (!_isOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(780, 620), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("MCP Assistant###McpAssistantWin", ref _isOpen, ImGuiWindowFlags.MenuBar | ImGuiWindowFlags.NoScrollbar))
        {
            DrawMenuBar();
            DrawSettingsSection();
            DrawChatLog();
            DrawPromptBar();
        }

        ImGui.End();
    }

    // ── Menu Bar ─────────────────────────────────────────────────────────

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMenuBar())
            return;

        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.MenuItem("Copy Last Response", null, false, _history.Count > 0))
                CopyLastResponse();

            if (ImGui.MenuItem("Copy Full History", null, false, _history.Count > 0))
                CopyFullHistory();

            ImGui.Separator();

            if (ImGui.MenuItem("Clear History", null, false, _history.Count > 0))
                _history.Clear();

            if (ImGui.MenuItem("Clear MCP Usage History", null, false, _mcpUsageHistory.Count > 0))
                _mcpUsageHistory.Clear();

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Settings"))
        {
            bool autoScroll = AutoScroll;
            if (ImGui.MenuItem("Auto-scroll on stream", null, ref autoScroll))
                AutoScroll = autoScroll;
            ImGui.EndMenu();
        }

        // Right-aligned status indicator in menu bar.
        float statusWidth = ImGui.CalcTextSize(_status).X + 20f;
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - statusWidth);
        ImGui.TextColored(_statusColor, _status);

        ImGui.EndMenuBar();
    }

    // ── Provider / MCP Settings (collapsible) ────────────────────────────

    private void DrawSettingsSection()
    {
        if (!ImGui.CollapsingHeader("Provider & MCP Settings"))
            return;

        ImGui.Indent(8f);

        // Provider selector — snapshot into locals for ImGui ref params, write back.
        int providerIndex = ProviderIndex;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.Combo("Provider", ref providerIndex, ProviderLabels, ProviderLabels.Length))
            ProviderIndex = providerIndex;

        ProviderType provider = (ProviderType)providerIndex;

        switch (provider)
        {
            case ProviderType.Codex:
            {
                string previousOpenAiKey = OpenAiApiKey;
                string oaiKey = OpenAiApiKey;
                DrawSecretField("API Key##OAI", ref oaiKey, 320f);
                OpenAiApiKey = oaiKey;
                if (!string.Equals(previousOpenAiKey, oaiKey, StringComparison.Ordinal))
                {
                    _openAiModelsAutoRequested = false;
                    lock (_openAiModelLock)
                        _openAiTextModels = [];
                    _openAiModelsStatus = "Not loaded";
                }

                string oaiModel = OpenAiModel;
                ImGui.SetNextItemWidth(220f);
                if (ImGui.InputText("Model##OAI", ref oaiModel, 128))
                    OpenAiModel = oaiModel;
                if (ImGuiTextFieldHelper.DrawTextFieldContextMenu("ctx_ModelOAI", ref oaiModel))
                    OpenAiModel = oaiModel;

                DrawOpenAiTextModelPicker();

                bool useRt = UseRealtimeWebSocket;
                if (ImGui.Checkbox("Use Realtime WebSocket", ref useRt))
                    UseRealtimeWebSocket = useRt;

                if (useRt)
                {
                    string rtModel = OpenAiRealtimeModel;
                    ImGui.SetNextItemWidth(220f);
                    if (ImGui.InputText("Realtime Model##OAI", ref rtModel, 128))
                        OpenAiRealtimeModel = rtModel;
                    if (ImGuiTextFieldHelper.DrawTextFieldContextMenu("ctx_RtModelOAI", ref rtModel))
                        OpenAiRealtimeModel = rtModel;
                }
                break;
            }

            case ProviderType.ClaudeCode:
            {
                string antKey = AnthropicApiKey;
                DrawSecretField("API Key##Ant", ref antKey, 320f);
                AnthropicApiKey = antKey;

                string antModel = AnthropicModel;
                ImGui.SetNextItemWidth(220f);
                if (ImGui.InputText("Model##Ant", ref antModel, 128))
                    AnthropicModel = antModel;
                if (ImGuiTextFieldHelper.DrawTextFieldContextMenu("ctx_ModelAnt", ref antModel))
                    AnthropicModel = antModel;
                break;
            }
        }

        int maxTokens = MaxTokens;
        ImGui.SetNextItemWidth(140f);
        if (ImGui.InputInt("Max Tokens", ref maxTokens, 256, 1024))
            MaxTokens = Math.Clamp(maxTokens, 64, 128_000);

        if (ImGui.Button("Load Keys from ENV"))
        {
            OpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? OpenAiApiKey;
            AnthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? AnthropicApiKey;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reads OPENAI_API_KEY and ANTHROPIC_API_KEY\nenvironment variables.");

        ImGui.Spacing();

        // MCP attachment
        bool attachMcp = AttachMcpServer;
        if (ImGui.Checkbox("Attach MCP Server", ref attachMcp))
            AttachMcpServer = attachMcp;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, the provider request includes\nyour local MCP server so the AI can call\neditor tools (list scenes, create nodes, etc.).");

        if (attachMcp)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Sync from Prefs"))
                RefreshMcpEndpointFromPreferences();

            ImGui.SetNextItemWidth(320f);
            ImGui.InputText("MCP URL", ref _mcpServerUrl, 1024);
            ImGuiTextFieldHelper.DrawTextFieldContextMenu("ctx_McpUrl", ref _mcpServerUrl);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("URL of the editor's built-in MCP server.\nDefault: http://localhost:5467/mcp/");

            DrawSecretField("MCP Auth Token##McpTok", ref _mcpAuthToken, 320f);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Optional. Only needed if you enabled\n'Require Auth' in Editor Preferences.\nThis is the bearer token the MCP server\nchecks on incoming requests.");
        }

        DrawMcpUsageHistorySection();

        ImGui.Unindent(8f);
        ImGui.Spacing();
    }

    private void DrawMcpUsageHistorySection()
    {
        if (!ImGui.CollapsingHeader("MCP Usage History"))
            return;

        if (_mcpUsageHistory.Count == 0)
        {
            ImGui.TextDisabled("No MCP usage records yet.");
            return;
        }

        ImGui.BeginChild("##McpUsageHistory", new Vector2(-1f, 150f), ImGuiChildFlags.Border);

        for (int i = _mcpUsageHistory.Count - 1; i >= 0; i--)
        {
            McpUsageEntry entry = _mcpUsageHistory[i];

            Vector4 resultColor = string.Equals(entry.Result, "Done", StringComparison.OrdinalIgnoreCase)
                ? ColorDone
                : string.Equals(entry.Result, "Pending", StringComparison.OrdinalIgnoreCase)
                    ? ColorBusy
                    : ColorError;

            ImGui.TextColored(ColorTimestamp, $"{entry.Timestamp:HH:mm:ss}");
            ImGui.SameLine();
            ImGui.TextUnformatted(entry.Provider);
            ImGui.SameLine();
            ImGui.TextColored(resultColor, entry.Result);

            ImGui.TextWrapped($"prompt: {entry.PromptPreview}");
            ImGui.TextDisabled($"attach:{entry.AttachRequested} payload:{entry.McpPayloadIncluded} choice:{entry.ToolChoice} required:{entry.RequireToolUse} mcpEvents:{entry.McpEventCount} toolEvents:{entry.ToolEventCount} fallback:{entry.RetriedWithoutMcp}");

            if (entry.McpDiscoveryFailed || !string.IsNullOrWhiteSpace(entry.Note))
                ImGui.TextColored(ColorError, string.IsNullOrWhiteSpace(entry.Note) ? "MCP discovery failed." : entry.Note);

            if (i > 0)
                ImGui.Separator();
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// Draws a password InputText with inline Paste and Clear buttons and a right-click context menu.
    /// Works around ImGui backends that don't reliably forward Ctrl+V to password fields.
    /// </summary>
    private static void DrawSecretField(string label, ref string value, float fieldWidth)
    {
        ImGui.SetNextItemWidth(fieldWidth);
        ImGui.InputText(label, ref value, 1024, ImGuiInputTextFlags.Password);

        // Right-click context menu via shared helper
        ImGuiTextFieldHelper.DrawSecretFieldContextMenu($"ctx_{label}", ref value);

        // Inline Paste button
        ImGui.SameLine();
        if (ImGui.SmallButton($"Paste##{label}"))
            value = ImGui.GetClipboardText() ?? value;

        // Show a masked preview so the user knows something is there
        if (!string.IsNullOrEmpty(value))
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorReady, $"({value.Length} chars)");
        }
    }

    private void DrawOpenAiTextModelPicker()
    {
        string[] models;
        lock (_openAiModelLock)
            models = _openAiTextModels;

        if (!_openAiModelsLoading
            && models.Length == 0
            && !_openAiModelsAutoRequested
            && !string.IsNullOrWhiteSpace(OpenAiApiKey))
        {
            _openAiModelsAutoRequested = true;
            _ = RefreshOpenAiTextModelsAsync();
        }

        if (ImGui.SmallButton("Refresh OpenAI Models"))
            _ = RefreshOpenAiTextModelsAsync();

        ImGui.SameLine();
        if (_openAiModelsLoading)
            ImGui.TextDisabled("Loading…");
        else
            ImGui.TextDisabled(_openAiModelsStatus);

        string preview = OpenAiModel;
        if (string.IsNullOrWhiteSpace(preview))
            preview = "(manual model)";

        ImGui.SetNextItemWidth(320f);
        if (ImGui.BeginCombo("Available Models##OpenAi", preview))
        {
            if (models.Length == 0)
            {
                ImGui.TextDisabled("No models loaded. Click Refresh OpenAI Models.");
            }
            else
            {
                for (int i = 0; i < models.Length; i++)
                {
                    string model = models[i];
                    bool selected = string.Equals(model, OpenAiModel, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(model, selected))
                        OpenAiModel = model;
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }
    }

    private async Task RefreshOpenAiTextModelsAsync()
    {
        if (_openAiModelsLoading)
            return;

        string apiKey = OpenAiApiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _openAiModelsStatus = "OpenAI API key required";
            return;
        }

        _openAiModelsLoading = true;
        _openAiModelsStatus = "Loading…";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using HttpResponseMessage response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _openAiModelsStatus = ExtractOpenAiErrorSummary(body)
                    ?? $"Load failed ({(int)response.StatusCode})";
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            {
                _openAiModelsStatus = "No models returned";
                return;
            }

            var textModels = new List<string>();
            var allModels = new List<string>();

            foreach (JsonElement item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.String)
                    continue;

                string id = idEl.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                allModels.Add(id);
                if (IsLikelyTextModelId(id))
                    textModels.Add(id);
            }

            List<string> chosen = textModels.Count > 0 ? textModels : allModels;
            chosen.Sort(StringComparer.OrdinalIgnoreCase);

            lock (_openAiModelLock)
                _openAiTextModels = chosen.ToArray();

            _openAiModelsStatus = $"Loaded {_openAiTextModels.Length} models";
        }
        catch (Exception ex)
        {
            _openAiModelsStatus = $"Load failed: {ex.Message}";
        }
        finally
        {
            _openAiModelsLoading = false;
        }
    }

    private static bool IsLikelyTextModelId(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        string id = modelId.Trim().ToLowerInvariant();

        if (id.Contains("embedding")
            || id.Contains("moderation")
            || id.Contains("transcribe")
            || id.Contains("tts")
            || id.Contains("realtime")
            || id.Contains("audio")
            || id.Contains("image")
            || id.StartsWith("dall-e", StringComparison.Ordinal)
            || id.StartsWith("sora", StringComparison.Ordinal)
            || id.StartsWith("whisper", StringComparison.Ordinal))
            return false;

        return true;
    }

    // ── Chat Log ─────────────────────────────────────────────────────────

    private void DrawChatLog()
    {
        // Reserve space for the prompt bar at the bottom.
        float promptBarHeight = GetPromptInputHeight() + 44f;
        float available = ImGui.GetContentRegionAvail().Y - promptBarHeight;
        if (available < 80f)
            available = 80f;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, ColorChatBg);
        ImGui.BeginChild("##ChatLog", new Vector2(-1f, available), ImGuiChildFlags.Border);

        if (_history.Count == 0)
        {
            // Keep the placeholder pinned and high-contrast so it is always visible.
            ImGui.SetCursorPos(new Vector2(12f, 12f));
            ImGui.TextColored(ColorPlaceholder, "Send a prompt to begin a conversation.");
        }
        else
        {
            ImGui.Spacing();
            for (int i = 0; i < _history.Count; i++)
            {
                ChatMessage msg = _history[i];
                bool isUser = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase);

                if (isUser)
                    DrawUserMessage(msg);
                else
                    DrawAssistantMessage(msg, i);

                // Thin separator between messages.
                if (i < _history.Count - 1)
                {
                    ImGui.Spacing();
                    var dl = ImGui.GetWindowDrawList();
                    Vector2 p = ImGui.GetCursorScreenPos();
                    float w = ImGui.GetContentRegionAvail().X;
                    dl.AddLine(p, p + new Vector2(w, 0), ImGui.ColorConvertFloat4ToU32(ColorSeparator));
                    ImGui.Spacing();
                    ImGui.Spacing();
                }
            }
        }

        // Auto-scroll when streaming or when a new message was just added.
        if (_scrollToBottom || (AutoScroll && _isBusy))
        {
            ImGui.SetScrollHereY(1.0f);
            _scrollToBottom = false;
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    // ── Copilot-Style Message Rendering ──────────────────────────────────

    private static void DrawUserMessage(ChatMessage msg)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        Vector2 startPos = ImGui.GetCursorScreenPos();
        float startY = ImGui.GetCursorPosY();

        ImGui.Indent(8f);

        // Header: "You" + timestamp.
        ImGui.TextColored(ColorUser, "You");
        ImGui.SameLine();
        ImGui.TextColored(ColorTimestamp, msg.Timestamp.ToString("HH:mm:ss"));

        ImGui.Spacing();

        // User message body.
        ImGui.PushTextWrapPos(0.0f);
        ImGui.TextUnformatted(msg.Content);
        ImGui.PopTextWrapPos();

        ImGui.Unindent(8f);
        ImGui.Spacing();

        // Draw subtle background bubble behind the rendered content.
        float endY = ImGui.GetCursorPosY();
        float height = endY - startY;
        float width = ImGui.GetContentRegionAvail().X + 16f;
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(
            startPos - new Vector2(4f, 2f),
            startPos + new Vector2(width, height - 2f),
            ImGui.ColorConvertFloat4ToU32(ColorUserBubble),
            6f);

        drawList.ChannelsMerge();
    }

    private void DrawAssistantMessage(ChatMessage msg, int index)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        Vector2 startPos = ImGui.GetCursorScreenPos();
        float startY = ImGui.GetCursorPosY();

        ImGui.Indent(8f);

        // Header: "Assistant" + timestamp + streaming indicator.
        ImGui.TextColored(ColorAssistant, "Assistant");
        ImGui.SameLine();
        ImGui.TextColored(ColorTimestamp, msg.Timestamp.ToString("HH:mm:ss"));

        if (msg.IsStreaming)
        {
            ImGui.SameLine();
            int dots = ((int)(ImGui.GetTime() * 3.0)) % 4;
            ImGui.TextColored(ColorBusy, "Working" + new string('.', dots + 1));
        }

        ImGui.Spacing();

        // Tool calls section (collapsible, rendered before main text content).
        if (msg.ToolCalls.Count > 0)
            DrawToolCallsSection(msg.ToolCalls, index);

        // Main rich-text content.
        string displayContent = GetDisplayContent(msg.Content);
        if (!string.IsNullOrWhiteSpace(displayContent))
            RenderRichContent(displayContent);

        ImGui.Unindent(8f);
        ImGui.Spacing();

        // Draw subtle background bubble.
        float endY = ImGui.GetCursorPosY();
        float height = endY - startY;
        float width = ImGui.GetContentRegionAvail().X + 16f;
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(
            startPos - new Vector2(4f, 2f),
            startPos + new Vector2(width, height - 2f),
            ImGui.ColorConvertFloat4ToU32(ColorAssistantBubble),
            6f);

        drawList.ChannelsMerge();
    }

    /// <summary>
    /// Renders the collapsible tool calls section, styled like Copilot Chat's
    /// expandable "Used N references" / "Working..." blocks.
    /// </summary>
    private static void DrawToolCallsSection(List<ToolCallEntry> toolCalls, int messageIndex)
    {
        int completed = toolCalls.Count(t => t.IsComplete);
        int errors = toolCalls.Count(t => t.IsError);
        bool allDone = completed == toolCalls.Count;

        // Section header with count badge.
        string headerLabel = allDone
            ? $"Used {toolCalls.Count} tool call{(toolCalls.Count != 1 ? "s" : "")}"
            : $"Running {toolCalls.Count - completed} of {toolCalls.Count} tool call{(toolCalls.Count != 1 ? "s" : "")}...";

        if (errors > 0)
            headerLabel += $" ({errors} failed)";

        ImGui.PushStyleColor(ImGuiCol.Header, ColorToolSection);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ColorToolSectionHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, ColorToolSectionHover);

        if (allDone)
        {
            ImGui.TextColored(ColorDone, "v");
            ImGui.SameLine();
        }

        bool open = ImGui.TreeNodeEx($"{headerLabel}##tools_{messageIndex}",
            ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen);

        ImGui.PopStyleColor(3);

        if (!open)
            return;

        foreach (ToolCallEntry call in toolCalls)
        {
            // Status icon.
            if (!call.IsComplete)
            {
                int dots = ((int)(ImGui.GetTime() * 2.5)) % 3;
                ImGui.TextColored(ColorBusy, new string('.', dots + 1));
            }
            else if (call.IsError)
                ImGui.TextColored(ColorError, "x");
            else
                ImGui.TextColored(ColorDone, "v");

            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, call.ToolName);

            if (!string.IsNullOrEmpty(call.ArgsSummary))
            {
                ImGui.SameLine();
                ImGui.TextColored(ColorTimestamp, $"({call.ArgsSummary})");
            }

            if (call.IsComplete && !string.IsNullOrEmpty(call.ResultSummary))
            {
                ImGui.SameLine();
                ImGui.TextColored(ColorTimestamp, "->");
                ImGui.SameLine();
                ImGui.TextColored(
                    call.IsError ? ColorError : new Vector4(0.55f, 0.78f, 0.55f, 1f),
                    call.ResultSummary);
            }
        }

        ImGui.TreePop();
        ImGui.Spacing();
    }

    /// <summary>
    /// Strips the inline "Tool calls:" text section from content since tool calls
    /// are rendered separately via the structured <see cref="ChatMessage.ToolCalls"/> list.
    /// Also strips trailing status markers like "[Executing tool calls...]".
    /// </summary>
    private static string GetDisplayContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        // Remove "Tool calls:\n  - ..." section.
        int toolIdx = content.IndexOf("Tool calls:", StringComparison.Ordinal);
        string text = toolIdx >= 0 ? content[..toolIdx].TrimEnd() : content;

        // Remove trailing status markers like "[Executing tool calls...]".
        int bracketIdx = text.LastIndexOf('[');
        if (bracketIdx >= 0 && text.TrimEnd().EndsWith(']'))
        {
            string candidate = text[bracketIdx..];
            if (candidate.Contains("tool call", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("model response", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("Calling tool", StringComparison.OrdinalIgnoreCase))
            {
                text = text[..bracketIdx].TrimEnd();
            }
        }

        return text;
    }

    // ── Rich Content Renderer (Markdown-lite for ImGui) ──────────────────

    /// <summary>
    /// Renders text with basic markdown formatting:
    /// fenced code blocks, headers, bullet points, inline backtick code, and **bold**.
    /// </summary>
    private static void RenderRichContent(string content)
    {
        string[] lines = content.Split('\n');
        bool inCodeBlock = false;

        for (int li = 0; li < lines.Length; li++)
        {
            string rawLine = lines[li].TrimEnd('\r');

            // Fenced code block toggle.
            if (rawLine.TrimStart().StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                ImGui.Spacing();
                continue;
            }

            if (inCodeBlock)
            {
                ImGui.Indent(12f);
                ImGui.TextColored(ColorCodeFg, rawLine);
                ImGui.Unindent(12f);
                continue;
            }

            // Empty line -> spacing.
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                ImGui.Spacing();
                continue;
            }

            string trimmed = rawLine.TrimStart();

            // Headers.
            if (trimmed.StartsWith("### "))
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorHeader, trimmed[4..]);
                continue;
            }
            if (trimmed.StartsWith("## "))
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorHeader, trimmed[3..]);
                continue;
            }
            if (trimmed.StartsWith("# "))
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorHeader, trimmed[2..]);
                continue;
            }

            // Bullet points.
            if (trimmed.Length > 2 && (trimmed[0] == '-' || trimmed[0] == '*') && trimmed[1] == ' ')
            {
                int indentChars = rawLine.Length - trimmed.Length;
                float indent = indentChars * 4f + 12f;
                ImGui.Indent(indent);
                ImGui.TextColored(ColorBullet, "*");
                ImGui.SameLine();
                RenderInlineFormatted(trimmed[2..]);
                ImGui.Unindent(indent);
                continue;
            }

            // Regular line with inline formatting.
            RenderInlineFormatted(rawLine);
        }
    }

    /// <summary>
    /// Renders a single line with inline backtick code and **bold** formatting.
    /// Falls back to plain word-wrapped text when no formatting markers are found.
    /// </summary>
    private static void RenderInlineFormatted(string text)
    {
        // Fast path: no inline markers.
        if (!text.Contains('`') && !text.Contains("**", StringComparison.Ordinal))
        {
            ImGui.PushTextWrapPos(0.0f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            return;
        }

        int pos = 0;
        bool first = true;

        while (pos < text.Length)
        {
            int backtick = text.IndexOf('`', pos);
            int bold = text.IndexOf("**", pos, StringComparison.Ordinal);

            // Find the nearest special marker.
            int next = -1;
            bool isBacktick = false;

            if (backtick >= 0 && (bold < 0 || backtick <= bold))
            {
                next = backtick;
                isBacktick = true;
            }
            else if (bold >= 0)
            {
                next = bold;
            }

            if (next < 0)
            {
                // No more markers -- render the remainder.
                string rest = text[pos..];
                if (rest.Length > 0)
                {
                    if (!first) ImGui.SameLine(0, 0);
                    ImGui.TextUnformatted(rest);
                }
                break;
            }

            // Render plain text before the marker.
            if (next > pos)
            {
                string before = text[pos..next];
                if (!first) ImGui.SameLine(0, 0);
                ImGui.TextUnformatted(before);
                first = false;
                pos = next;
            }

            if (isBacktick)
            {
                int close = text.IndexOf('`', pos + 1);
                if (close < 0)
                {
                    if (!first) ImGui.SameLine(0, 0);
                    ImGui.TextUnformatted(text[pos..]);
                    break;
                }

                string code = text[(pos + 1)..close];
                if (!first) ImGui.SameLine(0, 0);
                RenderCodeSpan(code);
                first = false;
                pos = close + 1;
            }
            else
            {
                int close = text.IndexOf("**", pos + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    if (!first) ImGui.SameLine(0, 0);
                    ImGui.TextUnformatted(text[pos..]);
                    break;
                }

                string boldText = text[(pos + 2)..close];
                if (!first) ImGui.SameLine(0, 0);
                ImGui.TextColored(ColorBold, boldText);
                first = false;
                pos = close + 2;
            }
        }
    }

    /// <summary>
    /// Renders an inline code span with a subtle background rectangle.
    /// </summary>
    private static void RenderCodeSpan(string code)
    {
        Vector2 textSize = ImGui.CalcTextSize(code);
        Vector2 cursorPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        const float padX = 3f;
        const float padY = 1f;
        drawList.AddRectFilled(
            cursorPos - new Vector2(padX, padY),
            cursorPos + textSize + new Vector2(padX, padY),
            ImGui.ColorConvertFloat4ToU32(ColorCodeBlockBg),
            3f);

        ImGui.TextColored(ColorCodeFg, code);
    }

    // ── Prompt Bar ───────────────────────────────────────────────────────

    private void DrawPromptBar()
    {
        // Button row
        bool canSend = !_isBusy && _prompt.Trim().Length > 0;

        if (!canSend)
            ImGui.BeginDisabled();
        bool sendClicked = ImGui.Button("Send (Enter)");
        if (!canSend)
            ImGui.EndDisabled();

        ImGui.SameLine();

        if (_isBusy)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.3f, 0.3f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.9f, 0.1f, 0.1f, 1f));
            if (ImGui.Button("Cancel"))
                _cts?.Cancel();
            ImGui.PopStyleColor(3);
        }

        // Char count
        ImGui.SameLine();
        ImGui.TextColored(ColorTimestamp, $"{_prompt.Length:N0} chars");

        // Right-aligned copy button
        if (_history.Count > 0)
        {
            float copyWidth = ImGui.CalcTextSize("Copy Last").X + 16f;
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - copyWidth + ImGui.GetCursorPosX());
            if (ImGui.SmallButton("Copy Last"))
                CopyLastResponse();
        }

        // Prompt input (fills width and grows with content)
        float promptInputHeight = GetPromptInputHeight();

        // Force-clear ImGui's internal text buffer after a send.
        // When the InputTextMultiline widget is active (focused), ImGui maintains
        // its own internal buffer and ignores external changes to the ref parameter.
        // To work around this, we render the widget with a temporary ID for one frame.
        // ImGui sees a brand-new widget and initializes it from the empty _prompt.
        // Next frame the normal ID is used again and picks up the cleared value.
        bool clearingThisFrame = _pendingPromptClear;
        if (clearingThisFrame)
        {
            _prompt = string.Empty;
            _pendingPromptClear = false;
        }

        // Snapshot the prompt before the widget so we can detect newline insertion.
        string promptBefore = _prompt;

        ImGui.InputTextMultiline(
            clearingThisFrame ? "##McpPromptClr" : "##McpPromptInput",
            ref _prompt,
            1024 * 128,
            new Vector2(-1f, promptInputHeight),
            ImGuiInputTextFlags.AllowTabInput);
        ImGuiTextFieldHelper.DrawTextFieldContextMenu("ctx_Prompt", ref _prompt);

        // Detect Enter: the multiline widget inserts '\n' for every Enter press.
        // Compare the prompt before/after to detect a newline was just typed.
        bool newlineJustInserted = _prompt.Length > promptBefore.Length
            && _prompt.Length > 0
            && _prompt[^1] == '\n';

        // Shift detection — try every method since backends vary.
        var io = ImGui.GetIO();
        bool shiftHeld = io.KeyShift
            || ImGui.IsKeyDown(ImGuiKey.ModShift)
            || ImGui.IsKeyDown(ImGuiKey.LeftShift)
            || ImGui.IsKeyDown(ImGuiKey.RightShift);

        bool enterSend = newlineJustInserted && !shiftHeld;

        if (enterSend)
            StripOneTrailingNewline(ref _prompt);

        bool canSendNow = !_isBusy && _prompt.Trim().Length > 0;
        if ((sendClicked || enterSend) && canSendNow)
            _ = SendPromptAsync();
    }

    private float GetPromptInputHeight()
    {
        float width = Math.Max(120f, ImGui.GetContentRegionAvail().X - 8f);
        int explicitLines = 1;
        for (int i = 0; i < _prompt.Length; i++)
        {
            if (_prompt[i] == '\n')
                explicitLines++;
        }

        int wrappedLines = EstimateWrappedLineCount(_prompt, width);
        int lines = Math.Clamp(Math.Max(explicitLines, wrappedLines), 2, 12);
        return lines * ImGui.GetTextLineHeightWithSpacing() + 10f;
    }

    private static int EstimateWrappedLineCount(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text))
            return 1;

        int totalLines = 0;
        string[] rawLines = text.Split('\n');
        for (int i = 0; i < rawLines.Length; i++)
        {
            string line = rawLines[i];
            float lineWidth = ImGui.CalcTextSize(string.IsNullOrEmpty(line) ? " " : line).X;
            int wraps = Math.Max(1, (int)Math.Ceiling(lineWidth / Math.Max(1.0f, maxWidth)));
            totalLines += wraps;
        }

        return Math.Max(1, totalLines);
    }

    private static void StripOneTrailingNewline(ref string text)
    {
        if (text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            text = text[..^2];
            return;
        }

        if (text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal))
            text = text[..^1];
    }

    // ── Clipboard Helpers ────────────────────────────────────────────────

    private void CopyLastResponse()
    {
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_history[i].Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                ImGui.SetClipboardText(_history[i].Content);
                SetStatus("Copied.", ColorDone);
                return;
            }
        }
    }

    private void CopyFullHistory()
    {
        var sb = new StringBuilder();
        foreach (ChatMessage msg in _history)
        {
            sb.AppendLine($"[{msg.Timestamp:yyyy-MM-dd HH:mm:ss}] {msg.Role}:");
            sb.AppendLine(msg.Content);
            sb.AppendLine();
        }

        ImGui.SetClipboardText(sb.ToString());
        SetStatus("History copied.", ColorDone);
    }

    // ── Request Dispatch ─────────────────────────────────────────────────

    private async Task SendPromptAsync()
    {
        if (_isBusy)
            return;

        string trimmedPrompt = _prompt.Trim();
        if (string.IsNullOrWhiteSpace(trimmedPrompt))
        {
            SetStatus("Prompt is empty.", ColorError);
            return;
        }

        ProviderType provider = (ProviderType)ProviderIndex;
        if (provider == ProviderType.Codex && string.IsNullOrWhiteSpace(OpenAiApiKey))
        {
            SetStatus("OpenAI API key is required.", ColorError);
            return;
        }

        if (provider == ProviderType.ClaudeCode && string.IsNullOrWhiteSpace(AnthropicApiKey))
        {
            SetStatus("Anthropic API key is required.", ColorError);
            return;
        }

        // Clear prompt and mark busy immediately — before any await — so the
        // next UI frame sees the empty prompt box and won't double-send.
        _history.Add(new ChatMessage { Role = "user", Content = trimmedPrompt });
        _prompt = string.Empty;
        _pendingPromptClear = true; // Ensures the ImGui widget picks up the empty string next frame.
        _scrollToBottom = true;
        _isBusy = true;
        SetStatus("Preparing\u2026", ColorBusy);

        bool attachRequested = AttachMcpServer;
        if (attachRequested)
            EnsureMcpServerAutoEnabledForAssistantMessage();

        var mcpUsage = new McpUsageEntry
        {
            Provider = provider.ToString(),
            PromptPreview = trimmedPrompt.Length > 96 ? trimmedPrompt[..96] + "…" : trimmedPrompt,
            AttachRequested = attachRequested,
            Result = "Pending"
        };
        AddMcpUsageEntry(mcpUsage);

        if (attachRequested)
        {
            SetStatus("Validating MCP connection…", ColorBusy);
            using var preflightTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            (bool mcpReady, string reason) = await EnsureMcpServerAttachReadyAsync(preflightTimeout.Token);
            if (!mcpReady)
            {
                mcpUsage.Result = "MCP Unavailable";
                mcpUsage.Note = reason;
                SetStatus($"MCP unavailable: {reason}", ColorError);
                _isBusy = false;
                return;
            }
        }

        // Placeholder assistant message that will be filled by streaming.
        var assistantMsg = new ChatMessage { Role = "assistant", IsStreaming = true };
        _history.Add(assistantMsg);

        SetStatus("Streaming\u2026", ColorBusy);
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            switch (provider)
            {
                case ProviderType.Codex when UseRealtimeWebSocket:
                    mcpUsage.Note = "Realtime mode does not pass MCP tools array; only instructions include endpoint hint.";
                    await StreamOpenAiRealtimeAsync(trimmedPrompt, assistantMsg, _cts.Token, mcpUsage);
                    break;
                case ProviderType.Codex:
                    await StreamOpenAiResponsesAsync(trimmedPrompt, assistantMsg, _cts.Token, mcpUsage);
                    break;
                case ProviderType.ClaudeCode:
                    await StreamAnthropicAsync(trimmedPrompt, assistantMsg, _cts.Token, mcpUsage);
                    break;
            }

            assistantMsg.IsStreaming = false;
            if (string.IsNullOrWhiteSpace(assistantMsg.Content))
                assistantMsg.Content = "(No response content received.)";

            if (IsProviderErrorContent(assistantMsg.Content, out string? providerErrorSummary))
            {
                mcpUsage.Result = "Failed";
                SetStatus(providerErrorSummary ?? "Failed.", ColorError);
            }
            else
            {
                mcpUsage.Result = "Done";
                SetStatus("Done.", ColorDone);
            }
        }
        catch (OperationCanceledException)
        {
            assistantMsg.IsStreaming = false;
            if (string.IsNullOrWhiteSpace(assistantMsg.Content))
                assistantMsg.Content = "(Canceled.)";
            mcpUsage.Result = "Canceled";
            SetStatus("Canceled.", ColorError);
        }
        catch (Exception ex)
        {
            assistantMsg.IsStreaming = false;
            assistantMsg.Content += $"\n\n--- Error ---\n{ex}";
            mcpUsage.Result = "Error";
            mcpUsage.Note = ex.Message;
            SetStatus("Failed.", ColorError);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _isBusy = false;
        }
    }

    private void AddMcpUsageEntry(McpUsageEntry entry)
    {
        _mcpUsageHistory.Add(entry);
        if (_mcpUsageHistory.Count > MaxMcpUsageEntries)
            _mcpUsageHistory.RemoveAt(0);
    }

    private void EnsureMcpServerAutoEnabledForAssistantMessage()
    {
        EditorPreferences? prefs = Engine.EditorPreferences;
        if (prefs is null)
            return;

        RefreshMcpEndpointFromPreferences();

        if (!prefs.McpServerEnabled)
            prefs.McpServerEnabled = true;

        try
        {
            int port = Math.Clamp(prefs.McpServerPort, 1, 65535);
            if (!McpServerHost.Instance.IsRunning)
                McpServerHost.Instance.Start(port);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed auto-starting MCP server for assistant chat: {ex.Message}");
        }
    }

    private async Task<(bool ok, string reason)> EnsureMcpServerAttachReadyAsync(CancellationToken ct)
    {
        if (!AttachMcpServer)
            return (true, "MCP attachment disabled.");

        string url = _mcpServerUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            return (false, "Invalid MCP URL.");

        if (!McpServerHost.Instance.IsRunning)
        {
            EnsureMcpServerAutoEnabledForAssistantMessage();
            if (!McpServerHost.Instance.IsRunning)
                return (false, "MCP server is not running.");
        }

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "mcp-preflight-tools-list",
            ["method"] = "tools/list"
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(_mcpAuthToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mcpAuthToken.Trim());

            using HttpResponseMessage response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return (false, $"HTTP {(int)response.StatusCode}");

            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("error", out JsonElement err) && err.ValueKind == JsonValueKind.Object)
            {
                string msg = TryExtractEventErrorMessage(root) ?? "tools/list failed";
                return (false, msg);
            }

            if (!root.TryGetProperty("result", out _))
                return (false, "MCP server returned no result.");

            return (true, "ok");
        }
        catch (OperationCanceledException)
        {
            return (false, "MCP preflight timed out.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private void SetStatus(string text, Vector4 color)
    {
        _status = text;
        _statusColor = color;
    }

    // ── OpenAI Responses API — Streaming SSE with Local Tool-Use Loop ───

    private async Task StreamOpenAiResponsesAsync(string prompt, ChatMessage target, CancellationToken ct, McpUsageEntry usage)
    {
        bool requestLikelyNeedsTools = IsLikelySceneMutationPrompt(prompt);
        usage.RequireToolUse = requestLikelyNeedsTools;

        // If MCP is enabled, fetch tools from local MCP server and convert to OpenAI function tools.
        JsonArray? openAiFunctionTools = null;
        if (AttachMcpServer)
        {
            JsonArray? mcpTools = await FetchMcpToolListAsync(ct);
            if (mcpTools is null || mcpTools.Count == 0)
            {
                usage.McpPayloadIncluded = false;
                usage.McpDiscoveryFailed = true;
                usage.Result = "MCP Fetch Failed";
                target.Content = "MCP is enabled but no tools could be fetched from the local MCP server.";
                return;
            }

            openAiFunctionTools = ConvertMcpToolsToOpenAiFunctions(mcpTools);
            usage.McpPayloadIncluded = true;
            usage.ToolChoice = requestLikelyNeedsTools ? "required" : "auto";
        }

        // Build the conversation input — starts with just the user prompt, grows with tool call results.
        var conversationInput = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = prompt } };

        string model = string.IsNullOrWhiteSpace(OpenAiModel) ? "gpt-4o" : OpenAiModel;
        string instructions = BuildOpenAiInstructions(requestLikelyNeedsTools, attachMcp: AttachMcpServer);

        const int maxToolRounds = 10;
        var sb = new StringBuilder();
        var toolLog = new StringBuilder(); // Human-readable log of tool calls shown in the response.
        var seenEventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int sseLineCount = 0;

        for (int round = 0; round < maxToolRounds; round++)
        {
            var payload = new JsonObject
            {
                ["model"] = model,
                ["input"] = JsonNode.Parse(conversationInput.ToJsonString()),
                ["stream"] = true,
                ["instructions"] = instructions
            };

            if (openAiFunctionTools is { Count: > 0 })
            {
                payload["tools"] = JsonNode.Parse(openAiFunctionTools.ToJsonString());
                // Only require tool_choice on first round when mutation is likely
                if (round == 0 && requestLikelyNeedsTools)
                    payload["tool_choice"] = "required";
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OpenAiApiKey.Trim());

            using HttpResponseMessage response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(ct);
                usage.Result = $"HTTP {(int)response.StatusCode}";
                target.Content = sb.Length > 0
                    ? sb + $"\n\n--- API Error (round {round + 1}) ---\n{errorBody}"
                    : errorBody;
                return;
            }

            // If the response isn't SSE, read the whole body and extract text.
            string? contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                string body = await response.Content.ReadAsStringAsync(ct);
                string extracted = ExtractOpenAiResponseText(body);
                sb.Append(extracted);
                target.Content = sb.ToString();
                return;
            }

            // Stream SSE, collecting text deltas and function calls.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var pendingCalls = new List<PendingFunctionCall>();
            var callsByIndex = new Dictionary<int, PendingFunctionCall>();
            string? responseId = null;
            string? line;

            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                    continue;

                string data = line[6..];
                if (data is "[DONE]")
                    break;

                sseLineCount++;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    JsonElement root = doc.RootElement;

                    if (!root.TryGetProperty("type", out var typeEl))
                    {
                        seenEventTypes.Add("(no-type)");
                        // Legacy shape without "type" — try delta extraction
                        if (TryExtractDelta(root, out string? legacyDelta) && !string.IsNullOrEmpty(legacyDelta))
                        {
                            sb.Append(legacyDelta);
                            target.Content = sb.ToString();
                        }
                        continue;
                    }

                    string eventType = typeEl.GetString() ?? string.Empty;
                    seenEventTypes.Add(eventType);

                    // Track MCP/tool event counts for diagnostics
                    if (eventType.Contains("tool", StringComparison.OrdinalIgnoreCase)
                        || eventType.Contains("function", StringComparison.OrdinalIgnoreCase))
                        usage.ToolEventCount++;

                    // Extract response ID
                    if (string.Equals(eventType, "response.created", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("response", out var respEl)
                        && respEl.TryGetProperty("id", out var idEl))
                    {
                        responseId = idEl.GetString();
                    }

                    // Text delta — but skip function_call event types whose
                    // "delta" field contains argument JSON, not user-visible text.
                    if (!eventType.StartsWith("response.function_call", StringComparison.OrdinalIgnoreCase)
                        && TryExtractOpenAiSseText(root, out string? deltaText))
                    {
                        sb.Append(deltaText);
                        target.Content = sb.ToString();
                        continue;
                    }

                    // Function call started
                    if (string.Equals(eventType, "response.output_item.added", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("item", out var itemEl)
                        && itemEl.TryGetProperty("type", out var itemTypeEl)
                        && string.Equals(itemTypeEl.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
                    {
                        string callId = itemEl.TryGetProperty("call_id", out var cidEl) ? cidEl.GetString() ?? "" : "";
                        string name = itemEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                        int outputIndex = root.TryGetProperty("output_index", out var oiEl) ? oiEl.GetInt32() : -1;

                        var pending = new PendingFunctionCall { CallId = callId, Name = name };
                        pendingCalls.Add(pending);
                        if (outputIndex >= 0)
                            callsByIndex[outputIndex] = pending;

                        // Show the user which tools the model is planning to call.
                        target.Content = sb.Length > 0
                            ? sb + $"\n\n[Calling tool: {name}...]" 
                            : $"[Calling tool: {name}...]";
                    }

                    // Function call arguments delta
                    if (string.Equals(eventType, "response.function_call_arguments.delta", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("delta", out var argDelta)
                        && argDelta.ValueKind == JsonValueKind.String)
                    {
                        int idx = root.TryGetProperty("output_index", out var oIdx) ? oIdx.GetInt32() : -1;
                        if (callsByIndex.TryGetValue(idx, out var pc))
                            pc.Arguments.Append(argDelta.GetString());
                    }

                    // Function call arguments done (complete string)
                    if (string.Equals(eventType, "response.function_call_arguments.done", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("arguments", out var argsDone)
                        && argsDone.ValueKind == JsonValueKind.String)
                    {
                        int idx = root.TryGetProperty("output_index", out var oIdx) ? oIdx.GetInt32() : -1;
                        if (callsByIndex.TryGetValue(idx, out var pc))
                        {
                            pc.Arguments.Clear();
                            pc.Arguments.Append(argsDone.GetString());
                        }
                    }

                    // Response completed — use exact event types to avoid
                    // breaking on per-item events like response.output_item.done
                    // or response.function_call_arguments.done.
                    if (string.Equals(eventType, "response.completed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(eventType, "response.done", StringComparison.OrdinalIgnoreCase))
                    {
                        if (sb.Length == 0)
                            ExtractCompletedResponseText(root, sb);
                        break;
                    }

                    // Response failed
                    if (eventType.Contains("failed", StringComparison.OrdinalIgnoreCase))
                    {
                        string? errMsg = TryExtractEventErrorMessage(root);
                        if (!string.IsNullOrWhiteSpace(errMsg))
                            sb.Append($"\n\n--- API Error ---\n{errMsg}");
                        break;
                    }
                }
                catch (JsonException)
                {
                    // Malformed SSE line — skip.
                }
            }

            // If no function calls were collected, we're done.
            if (pendingCalls.Count == 0)
                break;

            // Execute function calls locally against the MCP server.
            string toolNames = string.Join(", ", pendingCalls.Select(c => c.Name));
            target.Content = sb.Length > 0
                ? sb + $"\n\n[Executing {pendingCalls.Count} tool(s): {toolNames}...]"
                : $"[Executing {pendingCalls.Count} tool(s): {toolNames}...]";
            usage.ToolEventCount += pendingCalls.Count;

            foreach (var call in pendingCalls)
            {
                var tcEntry = new ToolCallEntry
                {
                    ToolName = FormatToolName(call.Name),
                    ArgsSummary = SummarizeToolArguments(call.Name, call.Arguments.ToString()),
                };
                target.ToolCalls.Add(tcEntry);

                string toolResult = await ExecuteMcpToolCallAsync(call.Name, call.Arguments.ToString(), ct);
                usage.McpEventCount++;

                tcEntry.ResultSummary = SummarizeToolResult(toolResult);
                tcEntry.IsError = toolResult.StartsWith("[MCP Error]", StringComparison.Ordinal);
                tcEntry.IsComplete = true;

                AppendToolCallLog(toolLog, call.Name, call.Arguments.ToString(), toolResult);
                target.Content = BuildAssistantContent(sb, toolLog, "Executing tool calls...");

                // Append the function call and its result to the conversation for the next round.
                conversationInput.Add(new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = call.CallId,
                    ["name"] = call.Name,
                    ["arguments"] = call.Arguments.ToString()
                });
                conversationInput.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = call.CallId,
                    ["output"] = toolResult
                });
            }

            // Continue the loop — next round will send conversation with tool results.
            target.Content = BuildAssistantContent(sb, toolLog, "Waiting for model response...");
        }

        if (sb.Length > 0 || toolLog.Length > 0)
            target.Content = BuildAssistantContent(sb, toolLog);
        else if (string.IsNullOrWhiteSpace(target.Content))
        {
            string events = seenEventTypes.Count > 0
                ? string.Join(", ", seenEventTypes)
                : "none";
            target.Content = $"(No response content received from the API. SSE lines: {sseLineCount}, event types seen: {events})";
        }
    }

    /// <summary>
    /// Tries to extract delta text from an SSE event JSON object.
    /// Handles multiple OpenAI event shapes:
    ///   { "delta": "text" }
    ///   { "delta": { "text": "text" } }
    ///   { "delta": { "content": "text" } }
    ///   { "delta": { "value": "text" } }
    /// </summary>
    private static bool TryExtractDelta(JsonElement root, out string? text)
    {
        text = null;
        if (!root.TryGetProperty("delta", out var delta))
            return false;

        // Direct string delta: { "delta": "hello" }
        if (delta.ValueKind == JsonValueKind.String)
        {
            text = delta.GetString();
            return text is not null;
        }

        // Object delta — try common field names
        if (delta.ValueKind == JsonValueKind.Object)
        {
            string[] fieldNames = ["text", "content", "value"];
            foreach (string field in fieldNames)
            {
                if (delta.TryGetProperty(field, out var inner) && inner.ValueKind == JsonValueKind.String)
                {
                    text = inner.GetString();
                    if (text is not null)
                        return true;
                }
            }
        }

        return false;
    }

    private static bool TryExtractOpenAiSseText(JsonElement root, out string? text)
    {
        text = null;

        if (TryExtractDelta(root, out text) && !string.IsNullOrEmpty(text))
            return true;

        if (root.TryGetProperty("type", out var typeEl))
        {
            string eventType = typeEl.GetString() ?? string.Empty;

            if ((string.Equals(eventType, "response.output_text.delta", StringComparison.OrdinalIgnoreCase)
                || string.Equals(eventType, "response.text.delta", StringComparison.OrdinalIgnoreCase))
                && root.TryGetProperty("delta", out var deltaEl)
                && deltaEl.ValueKind == JsonValueKind.String)
            {
                text = deltaEl.GetString();
                return !string.IsNullOrEmpty(text);
            }

            if (string.Equals(eventType, "response.output_item.added", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("item", out var itemEl)
                && itemEl.ValueKind == JsonValueKind.Object)
            {
                if (TryExtractTextFromOutputItem(itemEl, out text))
                    return true;
            }
        }

        return false;
    }

    private static bool TryExtractTextFromOutputItem(JsonElement item, out string? text)
    {
        text = null;

        if (item.TryGetProperty("type", out var itemTypeEl))
        {
            string itemType = itemTypeEl.GetString() ?? string.Empty;
            if (!string.Equals(itemType, "message", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(itemType, "output_text", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var sb = new StringBuilder();
        if (item.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement block in contentEl.EnumerateArray())
            {
                if (block.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    sb.Append(textEl.GetString());
            }
        }

        if (sb.Length > 0)
        {
            text = sb.ToString();
            return true;
        }

        return false;
    }

    private static string? TryExtractEventErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
        {
            if (errEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                return msgEl.GetString();
        }

        if (root.TryGetProperty("message", out var topMsgEl) && topMsgEl.ValueKind == JsonValueKind.String)
            return topMsgEl.GetString();

        return null;
    }

    private static bool IsProviderErrorContent(string content, out string? summary)
    {
        summary = null;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        if (TryExtractJsonErrorSummary(content, out string? jsonSummary))
        {
            summary = jsonSummary;
            return true;
        }

        if (content.Contains("--- Retry Failed", StringComparison.OrdinalIgnoreCase)
            || content.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
            || content.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
            || content.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || content.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase))
        {
            summary = "Provider request failed.";
            return true;
        }

        return false;
    }

    private static string? ExtractOpenAiErrorSummary(string content)
        => TryExtractJsonErrorSummary(content, out string? summary) ? summary : null;

    private static bool TryExtractJsonErrorSummary(string content, out string? summary)
    {
        summary = null;

        // Quick pre-check: valid JSON must start with '{' or '[' (ignoring whitespace).
        ReadOnlySpan<char> trimmed = content.AsSpan().TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;

        // Reject concatenated JSON objects (e.g. "{...}{...}") — find the matching
        // close brace/bracket and ensure nothing significant follows it.
        if (!LooksLikeSingleJsonValue(trimmed))
            return false;

        // Use Utf8JsonReader to probe validity before committing to a full parse.
        // This avoids first-chance JsonReaderException noise in the debugger.
        ReadOnlySpan<byte> utf8 = System.Text.Encoding.UTF8.GetBytes(content);
        var readerCheck = new Utf8JsonReader(utf8);
        try
        {
            if (!JsonDocument.TryParseValue(ref readerCheck, out var maybeDoc) || maybeDoc is null)
                return false;
            using var doc = maybeDoc;
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("error", out var errEl) || errEl.ValueKind != JsonValueKind.Object)
                return false;

            string? message = null;
            string? code = null;

            if (errEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                message = msgEl.GetString();

            if (errEl.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
                code = codeEl.GetString();

            if (string.Equals(code, "insufficient_quota", StringComparison.OrdinalIgnoreCase))
            {
                summary = "OpenAI quota/billing issue (insufficient_quota).";
                return true;
            }

            summary = !string.IsNullOrWhiteSpace(message)
                ? $"OpenAI error: {message}"
                : "OpenAI request failed.";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Cheap heuristic: counts matched braces/brackets to check that the span
    /// contains a single JSON value and not concatenated objects like "{...}{...}".
    /// </summary>
    private static bool LooksLikeSingleJsonValue(ReadOnlySpan<char> trimmed)
    {
        char open = trimmed[0];
        char close = open == '{' ? '}' : ']';
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    // Anything non-whitespace after the closing bracket → not single value.
                    ReadOnlySpan<char> rest = trimmed[(i + 1)..].TrimStart();
                    return rest.Length == 0;
                }
            }
        }

        return false; // Unbalanced — not valid.
    }

    /// <summary>
    /// When a response.completed event arrives and we haven't streamed any deltas,
    /// try to pull output_text or output[].content[].text from the completed response object.
    /// </summary>
    private static void ExtractCompletedResponseText(JsonElement root, StringBuilder sb)
    {
        // { "response": { "output_text": "..." } }
        if (root.TryGetProperty("response", out var resp))
        {
            if (resp.TryGetProperty("output_text", out var otEl) && otEl.ValueKind == JsonValueKind.String)
            {
                string? ot = otEl.GetString();
                if (!string.IsNullOrWhiteSpace(ot))
                {
                    sb.Append(ot);
                    return;
                }
            }

            // { "response": { "output": [ { "content": [ { "text": "..." } ] } ] } }
            if (resp.TryGetProperty("output", out var outputArr) && outputArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in outputArr.EnumerateArray())
                {
                    if (item.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement block in contentArr.EnumerateArray())
                        {
                            if (block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                                sb.Append(txt.GetString());
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Extracts text from a non-streamed OpenAI Responses API JSON body.
    /// </summary>
    private static string ExtractOpenAiResponseText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            // Top-level output_text
            if (root.TryGetProperty("output_text", out var otEl) && otEl.ValueKind == JsonValueKind.String)
            {
                string? ot = otEl.GetString();
                if (!string.IsNullOrWhiteSpace(ot))
                    return ot;
            }

            // output[].content[].text
            if (root.TryGetProperty("output", out var outputArr) && outputArr.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (JsonElement item in outputArr.EnumerateArray())
                {
                    if (item.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement block in contentArr.EnumerateArray())
                        {
                            if (block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                                sb.Append(txt.GetString());
                        }
                    }
                }
                if (sb.Length > 0)
                    return sb.ToString();
            }

            // Deep search fallback
            if (TryExtractFirstTextNode(root, out string extracted))
                return extracted;
        }
        catch (JsonException)
        {
            // Not JSON — return raw.
        }

        return body;
    }

    // ── Anthropic Messages API — Streaming SSE with Local Tool-Use Loop ─

    private async Task StreamAnthropicAsync(string prompt, ChatMessage target, CancellationToken ct, McpUsageEntry usage)
    {
        // If MCP is enabled, fetch tools from local MCP server and convert to Anthropic format.
        JsonArray? anthropicTools = null;
        if (AttachMcpServer)
        {
            JsonArray? mcpTools = await FetchMcpToolListAsync(ct);
            if (mcpTools is null || mcpTools.Count == 0)
            {
                usage.McpPayloadIncluded = false;
                usage.McpDiscoveryFailed = true;
                usage.Result = "MCP Fetch Failed";
                target.Content = "MCP is enabled but no tools could be fetched from the local MCP server.";
                return;
            }

            anthropicTools = ConvertMcpToolsToAnthropicTools(mcpTools);
            usage.McpPayloadIncluded = true;
            usage.ToolChoice = "auto";
        }

        string model = string.IsNullOrWhiteSpace(AnthropicModel) ? "claude-sonnet-4-5" : AnthropicModel;

        // Build conversation messages — starts with user prompt, grows with tool results.
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = prompt }
        };

        const int maxToolRounds = 10;
        var sb = new StringBuilder();
        var toolLog = new StringBuilder(); // Human-readable log of tool calls shown in the response.
        var seenEventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int sseLineCount = 0;

        for (int round = 0; round < maxToolRounds; round++)
        {
            var payload = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = MaxTokens,
                ["stream"] = true,
                ["messages"] = JsonNode.Parse(messages.ToJsonString())
            };

            if (anthropicTools is { Count: > 0 })
                payload["tools"] = JsonNode.Parse(anthropicTools.ToJsonString());

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", AnthropicApiKey.Trim());
            request.Headers.Add("anthropic-version", "2023-06-01");

            using HttpResponseMessage response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(ct);
                usage.Result = $"HTTP {(int)response.StatusCode}";
                target.Content = sb.Length > 0
                    ? sb + $"\n\n--- API Error (round {round + 1}) ---\n{errorBody}"
                    : errorBody;
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            // Track tool_use blocks: index → (id, name, input JSON)
            var pendingToolUse = new List<(string Id, string Name, StringBuilder InputJson)>();
            int currentBlockIndex = -1;
            string? currentToolUseId = null;
            string? currentToolUseName = null;
            StringBuilder? currentToolUseInput = null;
            string? stopReason = null;
            string? line;

            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                    continue;

                string data = line[6..];
                if (data is "[DONE]")
                    break;

                sseLineCount++;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    JsonElement root = doc.RootElement;

                    if (!root.TryGetProperty("type", out var typeEl))
                    {
                        seenEventTypes.Add("(no-type)");
                        continue;
                    }

                    string eventType = typeEl.GetString() ?? string.Empty;
                    seenEventTypes.Add(eventType);

                    // Text delta
                    if (string.Equals(eventType, "content_block_delta", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("delta", out var deltaEl))
                    {
                        if (deltaEl.TryGetProperty("type", out var dtEl))
                        {
                            string deltaType = dtEl.GetString() ?? string.Empty;

                            if (string.Equals(deltaType, "text_delta", StringComparison.OrdinalIgnoreCase)
                                && deltaEl.TryGetProperty("text", out var textEl))
                            {
                                string? text = textEl.GetString();
                                if (text is not null)
                                {
                                    sb.Append(text);
                                    target.Content = sb.ToString();
                                }
                            }
                            else if (string.Equals(deltaType, "input_json_delta", StringComparison.OrdinalIgnoreCase)
                                && deltaEl.TryGetProperty("partial_json", out var pjEl))
                            {
                                currentToolUseInput?.Append(pjEl.GetString());
                            }
                        }
                        else if (deltaEl.TryGetProperty("text", out var textEl2))
                        {
                            // Fallback: delta without explicit type
                            string? text = textEl2.GetString();
                            if (text is not null)
                            {
                                sb.Append(text);
                                target.Content = sb.ToString();
                            }
                        }
                    }

                    // Content block start — check if it's a tool_use block
                    if (string.Equals(eventType, "content_block_start", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("content_block", out var blockEl)
                        && blockEl.TryGetProperty("type", out var blockTypeEl)
                        && string.Equals(blockTypeEl.GetString(), "tool_use", StringComparison.OrdinalIgnoreCase))
                    {
                        currentToolUseId = blockEl.TryGetProperty("id", out var bIdEl) ? bIdEl.GetString() ?? "" : "";
                        currentToolUseName = blockEl.TryGetProperty("name", out var bNameEl) ? bNameEl.GetString() ?? "" : "";
                        currentToolUseInput = new StringBuilder();
                        currentBlockIndex = root.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : -1;
                    }

                    // Content block stop — finalize tool_use if applicable
                    if (string.Equals(eventType, "content_block_stop", StringComparison.OrdinalIgnoreCase)
                        && currentToolUseId is not null)
                    {
                        pendingToolUse.Add((currentToolUseId, currentToolUseName ?? "", currentToolUseInput ?? new StringBuilder()));
                        currentToolUseId = null;
                        currentToolUseName = null;
                        currentToolUseInput = null;
                    }

                    // Message delta — check for stop_reason
                    if (string.Equals(eventType, "message_delta", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("delta", out var msgDelta)
                        && msgDelta.TryGetProperty("stop_reason", out var srEl))
                    {
                        stopReason = srEl.GetString();
                    }

                    if (string.Equals(eventType, "message_stop", StringComparison.OrdinalIgnoreCase))
                        break;

                    if (string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        usage.Result = "Failed";
                        sb.Append($"\n\n--- SSE Error ---\n{data}");
                        target.Content = sb.ToString();
                        return;
                    }
                }
                catch (JsonException)
                {
                    // Malformed SSE line — skip.
                }
            }

            // If the model didn't request any tool calls, we're done.
            if (pendingToolUse.Count == 0 || !string.Equals(stopReason, "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                usage.Result = "Done";
                break;
            }

            // Execute tool calls locally and prepare the next conversation turn.
            target.Content = BuildAssistantContent(sb, toolLog, "Executing tool calls...");
            usage.ToolEventCount += pendingToolUse.Count;

            // Build assistant message content (text + tool_use blocks)
            var assistantContent = new JsonArray();
            if (sb.Length > 0)
                assistantContent.Add(new JsonObject { ["type"] = "text", ["text"] = sb.ToString() });

            var toolResultContent = new JsonArray();

            foreach (var (toolId, toolName, inputJsonSb) in pendingToolUse)
            {
                string inputStr = inputJsonSb.ToString();

                // Add tool_use block to assistant message
                JsonNode? inputNode;
                try { inputNode = JsonNode.Parse(inputStr); }
                catch { inputNode = new JsonObject(); }

                assistantContent.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = toolId,
                    ["name"] = toolName,
                    ["input"] = inputNode
                });

                // Execute the tool call
                var tcEntry = new ToolCallEntry
                {
                    ToolName = FormatToolName(toolName),
                    ArgsSummary = SummarizeToolArguments(toolName, inputStr),
                };
                target.ToolCalls.Add(tcEntry);

                string toolResult = await ExecuteMcpToolCallAsync(toolName, inputStr, ct);
                usage.McpEventCount++;

                tcEntry.ResultSummary = SummarizeToolResult(toolResult);
                tcEntry.IsError = toolResult.StartsWith("[MCP Error]", StringComparison.Ordinal);
                tcEntry.IsComplete = true;

                AppendToolCallLog(toolLog, toolName, inputStr, toolResult);
                target.Content = BuildAssistantContent(sb, toolLog, "Executing tool calls...");

                // Add tool_result block for user message
                toolResultContent.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = toolId,
                    ["content"] = toolResult
                });
            }

            // Add assistant turn and user turn with tool results
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistantContent });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResultContent });

            target.Content = BuildAssistantContent(sb, toolLog, "Waiting for model response...");
            sb.Clear(); // Reset text accumulator for next round's text
        }

        if (sb.Length > 0 || toolLog.Length > 0)
            target.Content = BuildAssistantContent(sb, toolLog);
        else if (string.IsNullOrWhiteSpace(target.Content))
        {
            string events = seenEventTypes.Count > 0
                ? string.Join(", ", seenEventTypes)
                : "none";
            target.Content = $"(No response content received from the API. SSE lines: {sseLineCount}, event types seen: {events})";
        }
    }

    // ── OpenAI Realtime WebSocket — Already Streams ──────────────────────

    private async Task StreamOpenAiRealtimeAsync(string prompt, ChatMessage target, CancellationToken ct, McpUsageEntry usage)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {OpenAiApiKey.Trim()}");
        socket.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        string model = string.IsNullOrWhiteSpace(OpenAiRealtimeModel)
            ? "gpt-4o-realtime-preview"
            : OpenAiRealtimeModel.Trim();

        var uri = new Uri($"wss://api.openai.com/v1/realtime?model={Uri.EscapeDataString(model)}");
        await socket.ConnectAsync(uri, ct);

        await SendWsJsonAsync(socket, BuildRealtimeSessionUpdate(), ct);
        await SendWsJsonAsync(socket, BuildRealtimeUserMessage(prompt), ct);
        await SendWsJsonAsync(socket, BuildRealtimeResponseCreate(), ct);

        var sb = new StringBuilder();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            string? json = await ReceiveWsJsonAsync(socket, ct);
            if (string.IsNullOrWhiteSpace(json))
                continue;

            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl))
                continue;

            string eventType = typeEl.GetString() ?? string.Empty;

            if (string.Equals(eventType, "response.output_text.delta", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("delta", out var deltaEl))
            {
                sb.Append(deltaEl.GetString());
                target.Content = sb.ToString();
            }
            else if (string.Equals(eventType, "response.completed", StringComparison.OrdinalIgnoreCase))
            {
                usage.Result = "Done";
                break;
            }
            else if (string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
            {
                usage.Result = "Failed";
                target.Content = json;
                break;
            }
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    // ── WebSocket Helpers ────────────────────────────────────────────────

    private static async Task SendWsJsonAsync(ClientWebSocket socket, JsonObject payload, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string?> ReceiveWsJsonAsync(ClientWebSocket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[8192];
        var sb = new StringBuilder();

        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
                break;
        }

        return sb.ToString();
    }

    // ── Realtime Payload Builders ────────────────────────────────────────

    private JsonObject BuildRealtimeSessionUpdate()
    {
        string instructions = "Answer user requests about XREngine. Be concise and actionable.";
        if (AttachMcpServer)
            instructions += " Function tools for scene manipulation are available in the standard API path. The realtime path does not support tool calls.";

        return new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["instructions"] = instructions,
                ["modalities"] = new JsonArray("text")
            }
        };
    }

    private static JsonObject BuildRealtimeUserMessage(string prompt) => new()
    {
        ["type"] = "conversation.item.create",
        ["item"] = new JsonObject
        {
            ["type"] = "message",
            ["role"] = "user",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "input_text", ["text"] = prompt }
            }
        }
    };

    private static JsonObject BuildRealtimeResponseCreate() => new()
    {
        ["type"] = "response.create",
        ["response"] = new JsonObject { ["modalities"] = new JsonArray("text") }
    };

    private static string BuildOpenAiInstructions(bool requireToolUse, bool attachMcp)
    {
        string instructions = "You are an assistant for XREngine. Be concise and actionable.";

        if (attachMcp)
        {
            instructions += " You have function tools available that interact with the running XREngine editor scene. These tools allow you to list, create, modify, and delete scene nodes, components, and other editor objects.";

            if (requireToolUse)
            {
                instructions += " For scene-edit requests, call the appropriate function tools to perform the change first, then report what was changed.";
            }
            else
            {
                instructions += " Use the function tools when they materially improve correctness.";
            }
        }

        return instructions;
    }

    private static bool IsLikelySceneMutationPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        string text = prompt.Trim();
        string[] verbs =
        [
            "create", "add", "spawn", "place", "insert", "update", "set", "change", "modify",
            "move", "rotate", "scale", "reparent", "rename", "delete", "remove", "duplicate",
            "select", "load", "save", "import", "export"
        ];

        return verbs.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));
    }

    // ── MCP Local Proxy Helpers ──────────────────────────────────────────

    /// <summary>
    /// Calls the local MCP server's <c>tools/list</c> method and returns the tools array.
    /// Returns null on failure.
    /// </summary>
    private async Task<JsonArray?> FetchMcpToolListAsync(CancellationToken ct)
    {
        string url = _mcpServerUrl.Trim();
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "fetch-tools-list",
            ["method"] = "tools/list"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(_mcpAuthToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mcpAuthToken.Trim());

        using HttpResponseMessage response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        string body = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("result", out JsonElement result))
            return null;

        if (result.TryGetProperty("tools", out JsonElement toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
            return JsonSerializer.Deserialize<JsonArray>(toolsEl.GetRawText());

        return null;
    }

    /// <summary>
    /// Converts MCP tool definitions to OpenAI function tool format.
    /// </summary>
    private static JsonArray ConvertMcpToolsToOpenAiFunctions(JsonArray mcpTools)
    {
        var result = new JsonArray();
        foreach (JsonNode? tool in mcpTools)
        {
            if (tool is not JsonObject toolObj)
                continue;

            string? name = toolObj["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var fn = new JsonObject
            {
                ["type"] = "function",
                ["name"] = name
            };

            if (toolObj["description"] is JsonNode descNode)
                fn["description"] = descNode.GetValue<string>();

            // MCP uses "inputSchema", OpenAI uses "parameters"
            if (toolObj["inputSchema"] is JsonObject inputSchema)
                fn["parameters"] = JsonNode.Parse(inputSchema.ToJsonString());
            else
                fn["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

            result.Add(fn);
        }
        return result;
    }

    /// <summary>
    /// Converts MCP tool definitions to Anthropic tool format.
    /// </summary>
    private static JsonArray ConvertMcpToolsToAnthropicTools(JsonArray mcpTools)
    {
        var result = new JsonArray();
        foreach (JsonNode? tool in mcpTools)
        {
            if (tool is not JsonObject toolObj)
                continue;

            string? name = toolObj["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var anthropicTool = new JsonObject
            {
                ["name"] = name
            };

            if (toolObj["description"] is JsonNode descNode)
                anthropicTool["description"] = descNode.GetValue<string>();

            // MCP uses "inputSchema", Anthropic uses "input_schema"
            if (toolObj["inputSchema"] is JsonObject inputSchema)
                anthropicTool["input_schema"] = JsonNode.Parse(inputSchema.ToJsonString());
            else
                anthropicTool["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

            result.Add(anthropicTool);
        }
        return result;
    }

    // ── Tool Call Logging Helpers ─────────────────────────────────────────

    /// <summary>
    /// Appends a human-readable tool call entry to the log: tool name, key arguments, and a result snippet.
    /// </summary>
    private static void AppendToolCallLog(StringBuilder toolLog, string toolName, string argsJson, string result)
    {
        // Friendly tool name: replace underscores with spaces and title-case.
        string friendly = FormatToolName(toolName);

        // Extract a short description of what arguments were passed.
        string argsSummary = SummarizeToolArguments(toolName, argsJson);

        // Truncate the result for display.
        string resultSnippet = SummarizeToolResult(result);

        toolLog.Append($"\n  - {friendly}");
        if (!string.IsNullOrEmpty(argsSummary))
            toolLog.Append($" ({argsSummary})");
        if (!string.IsNullOrEmpty(resultSnippet))
            toolLog.Append($" -> {resultSnippet}");
    }

    /// <summary>
    /// Builds the final assistant content string from model text, tool log, and an optional trailing status.
    /// </summary>
    private static string BuildAssistantContent(StringBuilder modelText, StringBuilder toolLog, string? statusSuffix = null)
    {
        var result = new StringBuilder();

        if (modelText.Length > 0)
            result.Append(modelText);

        if (toolLog.Length > 0)
        {
            if (result.Length > 0)
                result.Append("\n\n");
            result.Append("Tool calls:");
            result.Append(toolLog);
        }

        if (!string.IsNullOrEmpty(statusSuffix))
        {
            if (result.Length > 0)
                result.Append("\n\n");
            result.Append($"[{statusSuffix}]");
        }

        return result.Length > 0 ? result.ToString() : string.Empty;
    }

    private static string FormatToolName(string toolName)
    {
        // "create_scene_node" → "Create scene node"
        string spaced = toolName.Replace('_', ' ');
        return spaced.Length > 0
            ? char.ToUpper(spaced[0]) + spaced[1..]
            : spaced;
    }

    private static string SummarizeToolArguments(string toolName, string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson) || argsJson is "{}" or "null")
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return string.Empty;

            // Pick the most meaningful argument to display.
            // Priority: name > node_name > scene_name > component_type > node_id > first string property
            string[] priorityKeys = ["name", "node_name", "scene_name", "component_type", "type", "node_id", "path"];
            foreach (string key in priorityKeys)
            {
                if (root.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    string? v = val.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                        return $"{key}: {Truncate(v, 60)}";
                }
            }

            // Fallback: show first string property.
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    string? v = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                        return $"{prop.Name}: {Truncate(v, 60)}";
                }
            }
        }
        catch { }

        return string.Empty;
    }

    private static string SummarizeToolResult(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return string.Empty;

        if (result.StartsWith("[MCP Error]", StringComparison.Ordinal))
            return Truncate(result, 80);

        // Try to extract a summary message from JSON results.
        ReadOnlySpan<char> trimmed = result.AsSpan().TrimStart();
        if (trimmed.Length > 0 && trimmed[0] == '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(result);
                var root = doc.RootElement;
                // Many MCP results have a top-level message or text.
                foreach (string key in new[] { "message", "text", "summary", "status" })
                {
                    if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        string? s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            return Truncate(s, 100);
                    }
                }
            }
            catch { }
        }

        // Plain text result — show a short snippet.
        return Truncate(result, 100);
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..(maxLen - 1)] + "\u2026";

    /// <summary>
    /// Executes a tool call against the local MCP server's <c>tools/call</c> endpoint.
    /// Returns the result text, or an error message on failure.
    /// </summary>
    private async Task<string> ExecuteMcpToolCallAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        string url = _mcpServerUrl.Trim();

        JsonNode? argsNode;
        try
        {
            argsNode = JsonNode.Parse(argumentsJson);
        }
        catch
        {
            argsNode = new JsonObject();
        }

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = $"tool-call-{Guid.NewGuid():N}",
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = argsNode
            }
        };

        try
        {
            // Apply a 30-second timeout for individual MCP tool calls to prevent indefinite hangs.
            using var toolTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            toolTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var linkedCt = toolTimeout.Token;

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(_mcpAuthToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mcpAuthToken.Trim());

            using HttpResponseMessage response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCt);
            string body = await response.Content.ReadAsStringAsync(linkedCt);

            if (!response.IsSuccessStatusCode)
                return $"[MCP Error] HTTP {(int)response.StatusCode}: {body}";

            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("error", out JsonElement errEl) && errEl.ValueKind == JsonValueKind.Object)
            {
                string? errMsg = errEl.TryGetProperty("message", out var m) ? m.GetString() : null;
                return $"[MCP Error] {errMsg ?? "Unknown error"}";
            }

            if (root.TryGetProperty("result", out JsonElement resultEl))
            {
                // Try to extract text from content array: { "content": [ { "type": "text", "text": "..." } ] }
                var sb = new StringBuilder();
                if (resultEl.TryGetProperty("content", out JsonElement contentArr) && contentArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in contentArr.EnumerateArray())
                    {
                        if (item.TryGetProperty("text", out JsonElement textEl) && textEl.ValueKind == JsonValueKind.String)
                        {
                            if (sb.Length > 0) sb.Append('\n');
                            sb.Append(textEl.GetString());
                        }
                    }
                }

                // Append structured data (e.g. { id, path }) so the model can
                // reference created resource IDs in subsequent tool calls.
                if (resultEl.TryGetProperty("data", out JsonElement dataEl)
                    && dataEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(dataEl.GetRawText());
                }

                if (sb.Length > 0) return sb.ToString();

                // Fallback: serialize the entire result
                return resultEl.GetRawText();
            }

            return body;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"[MCP Error] Tool call '{toolName}' timed out after 30 seconds.";
        }
        catch (Exception ex)
        {
            return $"[MCP Error] {ex.Message}";
        }
    }

    /// <summary>
    /// Helper record for collecting function calls from SSE events during streaming.
    /// </summary>
    private sealed class PendingFunctionCall
    {
        public string CallId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }

    // ── JSON Helpers ─────────────────────────────────────────────────────

    private static bool TryExtractFirstTextNode(JsonElement element, out string text)
    {
        text = string.Empty;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals("text") && property.Value.ValueKind == JsonValueKind.String)
                {
                    text = property.Value.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text))
                        return true;
                }

                if (TryExtractFirstTextNode(property.Value, out text))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                if (TryExtractFirstTextNode(child, out text))
                    return true;
            }
        }

        return false;
    }

    // ── Preferences Sync ─────────────────────────────────────────────────

    private void RefreshMcpEndpointFromPreferences()
    {
        int port = Math.Clamp(Engine.EditorPreferences?.McpServerPort ?? 5467, 1, 65535);
        _mcpServerUrl = $"http://localhost:{port}/mcp/";
        _mcpAuthToken = Engine.EditorPreferences?.McpServerAuthToken ?? string.Empty;
    }
}
