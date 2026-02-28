using ImGuiNET;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private sealed class ChatMessage
    {
        public required string Role { get; init; }
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public bool IsStreaming { get; set; }
    }

    // ── Constants ────────────────────────────────────────────────────────

    private static readonly string[] ProviderLabels =
    [
        "Codex (OpenAI)",
        "Claude Code (Anthropic)"
    ];

    private static readonly Vector4 ColorUser = new(0.40f, 0.72f, 1.00f, 1.00f);
    private static readonly Vector4 ColorAssistant = new(0.45f, 0.95f, 0.50f, 1.00f);
    private static readonly Vector4 ColorTimestamp = new(0.50f, 0.50f, 0.50f, 1.00f);
    private static readonly Vector4 ColorReady = new(0.60f, 0.60f, 0.60f, 1.00f);
    private static readonly Vector4 ColorBusy = new(1.00f, 0.85f, 0.20f, 1.00f);
    private static readonly Vector4 ColorDone = new(0.30f, 1.00f, 0.30f, 1.00f);
    private static readonly Vector4 ColorError = new(1.00f, 0.30f, 0.30f, 1.00f);

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
    private readonly List<ChatMessage> _history = [];
    private bool _isBusy;
    private string _status = "Ready";
    private Vector4 _statusColor = ColorReady;
    private bool _scrollToBottom;
    private CancellationTokenSource? _cts;

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

        if (ImGui.Begin("MCP Assistant###McpAssistantWin", ref _isOpen, ImGuiWindowFlags.MenuBar))
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
                string oaiKey = OpenAiApiKey;
                DrawSecretField("API Key##OAI", ref oaiKey, 320f);
                OpenAiApiKey = oaiKey;

                string oaiModel = OpenAiModel;
                ImGui.SetNextItemWidth(220f);
                if (ImGui.InputText("Model##OAI", ref oaiModel, 128))
                    OpenAiModel = oaiModel;
                if (ImGuiTextFieldHelper.DrawTextFieldContextMenu("ctx_ModelOAI", ref oaiModel))
                    OpenAiModel = oaiModel;

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

        ImGui.Unindent(8f);
        ImGui.Spacing();
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

    // ── Chat Log ─────────────────────────────────────────────────────────

    private void DrawChatLog()
    {
        // Reserve space for the prompt bar at the bottom.
        float promptBarHeight = 82f;
        float available = ImGui.GetContentRegionAvail().Y - promptBarHeight;
        if (available < 80f)
            available = 80f;

        ImGui.BeginChild("##ChatLog", new Vector2(-1f, available), ImGuiChildFlags.Border);

        if (_history.Count == 0)
        {
            // Centered placeholder hint
            Vector2 region = ImGui.GetContentRegionAvail();
            const string hint = "Send a prompt to begin a conversation.";
            Vector2 textSize = ImGui.CalcTextSize(hint);
            ImGui.SetCursorPos((region - textSize) * 0.5f);
            ImGui.TextColored(ColorTimestamp, hint);
        }
        else
        {
            for (int i = 0; i < _history.Count; i++)
            {
                ChatMessage msg = _history[i];
                bool isUser = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase);

                // Role header with timestamp
                ImGui.TextColored(isUser ? ColorUser : ColorAssistant, isUser ? "You" : "Assistant");
                ImGui.SameLine();
                ImGui.TextColored(ColorTimestamp, $"  {msg.Timestamp:HH:mm:ss}");

                // Streaming indicator
                if (!isUser && msg.IsStreaming)
                {
                    ImGui.SameLine();
                    int dots = ((int)(ImGui.GetTime() * 3.0)) % 4;
                    ImGui.TextColored(ColorBusy, new string('.', dots + 1));
                }

                // Message body (word-wrapped)
                ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
                ImGui.TextUnformatted(msg.Content);
                ImGui.PopTextWrapPos();

                // Separator between entries
                if (i < _history.Count - 1)
                {
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();
                }
            }
        }

        // Auto-scroll when streaming or when a new message was just added
        if (_scrollToBottom || (AutoScroll && _isBusy))
        {
            ImGui.SetScrollHereY(1.0f);
            _scrollToBottom = false;
        }

        ImGui.EndChild();
    }

    // ── Prompt Bar ───────────────────────────────────────────────────────

    private void DrawPromptBar()
    {
        // Button row
        bool canSend = !_isBusy && _prompt.Trim().Length > 0;

        if (!canSend)
            ImGui.BeginDisabled();
        bool sendClicked = ImGui.Button("Send (Ctrl+Enter)");
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

        // Prompt input (fills width, short height so chat log gets most space)
        bool ctrlEnter = ImGui.IsKeyPressed(ImGuiKey.Enter) &&
                         (ImGui.IsKeyDown(ImGuiKey.ModCtrl) || ImGui.IsKeyDown(ImGuiKey.ModSuper));

        ImGui.InputTextMultiline(
            "##McpPromptInput",
            ref _prompt,
            1024 * 128,
            new Vector2(-1f, 48f),
            ImGuiInputTextFlags.AllowTabInput);
        ImGuiTextFieldHelper.DrawTextFieldContextMenu("ctx_Prompt", ref _prompt);

        if ((sendClicked || ctrlEnter) && canSend)
            _ = SendPromptAsync();
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

        // Push user message into chat history.
        _history.Add(new ChatMessage { Role = "user", Content = trimmedPrompt });
        _prompt = string.Empty;
        _scrollToBottom = true;

        // Placeholder assistant message that will be filled by streaming.
        var assistantMsg = new ChatMessage { Role = "assistant", IsStreaming = true };
        _history.Add(assistantMsg);

        _isBusy = true;
        SetStatus("Streaming\u2026", ColorBusy);
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            switch (provider)
            {
                case ProviderType.Codex when UseRealtimeWebSocket:
                    await StreamOpenAiRealtimeAsync(trimmedPrompt, assistantMsg, _cts.Token);
                    break;
                case ProviderType.Codex:
                    await StreamOpenAiResponsesAsync(trimmedPrompt, assistantMsg, _cts.Token);
                    break;
                case ProviderType.ClaudeCode:
                    await StreamAnthropicAsync(trimmedPrompt, assistantMsg, _cts.Token);
                    break;
            }

            assistantMsg.IsStreaming = false;
            if (string.IsNullOrWhiteSpace(assistantMsg.Content))
                assistantMsg.Content = "(No response content received.)";

            if (IsProviderErrorContent(assistantMsg.Content, out string? providerErrorSummary))
                SetStatus(providerErrorSummary ?? "Failed.", ColorError);
            else
                SetStatus("Done.", ColorDone);
        }
        catch (OperationCanceledException)
        {
            assistantMsg.IsStreaming = false;
            if (string.IsNullOrWhiteSpace(assistantMsg.Content))
                assistantMsg.Content = "(Canceled.)";
            SetStatus("Canceled.", ColorError);
        }
        catch (Exception ex)
        {
            assistantMsg.IsStreaming = false;
            assistantMsg.Content += $"\n\n--- Error ---\n{ex}";
            SetStatus("Failed.", ColorError);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _isBusy = false;
        }
    }

    private void SetStatus(string text, Vector4 color)
    {
        _status = text;
        _statusColor = color;
    }

    // ── OpenAI Responses API — Streaming SSE ─────────────────────────────

    private async Task StreamOpenAiResponsesAsync(string prompt, ChatMessage target, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(OpenAiModel) ? "gpt-5-codex" : OpenAiModel,
            ["input"] = prompt,
            ["stream"] = true
        };

        if (AttachMcpServer && TryBuildOpenAiMcpTool(out var mcpTool))
        {
            payload["tools"] = new JsonArray(mcpTool);
            payload["tool_choice"] = "auto";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OpenAiApiKey.Trim());

        using HttpResponseMessage response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            target.Content = await response.Content.ReadAsStringAsync(ct);
            return;
        }

        // If the response isn't SSE (no streaming), read the whole body and extract text.
        string? contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not null && !contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            target.Content = ExtractOpenAiResponseText(body);
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var sb = new StringBuilder();
        var rawEvents = new StringBuilder();   // Capture raw SSE for diagnostics
        bool sawMcpListToolsFailure = false;
        string? mcpFailureMessage = null;
        string? line;

        while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            string data = line[6..];
            if (data is "[DONE]")
                break;

            rawEvents.AppendLine(data);

            try
            {
                using var doc = JsonDocument.Parse(data);
                JsonElement root = doc.RootElement;

                // Try extracting text from any known event shape.
                if (TryExtractOpenAiSseText(root, out string? deltaText))
                {
                    sb.Append(deltaText);
                    target.Content = sb.ToString();
                    continue;
                }

                // Check for terminal events.
                if (root.TryGetProperty("type", out var typeEl))
                {
                    string eventType = typeEl.GetString() ?? string.Empty;

                    if (string.Equals(eventType, "response.mcp_list_tools.failed", StringComparison.OrdinalIgnoreCase))
                    {
                        sawMcpListToolsFailure = true;
                        mcpFailureMessage = TryExtractEventErrorMessage(root);
                    }

                    if (eventType.Contains("completed", StringComparison.OrdinalIgnoreCase)
                        || eventType.Contains("failed", StringComparison.OrdinalIgnoreCase)
                        || eventType.Contains("done", StringComparison.OrdinalIgnoreCase))
                    {
                        // On completed, try to extract output_text from the full response object.
                        if (sb.Length == 0)
                            ExtractCompletedResponseText(root, sb);
                        break;
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed SSE line — skip.
            }
        }

        if (sb.Length > 0)
        {
            target.Content = sb.ToString();
        }
        else if (AttachMcpServer && sawMcpListToolsFailure)
        {
            string reason = string.IsNullOrWhiteSpace(mcpFailureMessage)
                ? "MCP tool discovery failed"
                : mcpFailureMessage;

            target.Content = $"(MCP attach failed: {reason}. Retrying without MCP tool attachment...)";
            await FetchOpenAiResponseWithoutMcpAsync(prompt, target, ct);
        }
        else
        {
            // Nothing extracted from SSE deltas — show the raw events so the user can diagnose.
            string raw = rawEvents.ToString();
            target.Content = !string.IsNullOrWhiteSpace(raw)
                ? $"No text deltas found in response. Raw SSE events:\n\n{raw}"
                : "(No SSE events received from the API.)";
        }
    }

    private async Task FetchOpenAiResponseWithoutMcpAsync(string prompt, ChatMessage target, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(OpenAiModel) ? "gpt-5-codex" : OpenAiModel,
            ["input"] = prompt,
            ["stream"] = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OpenAiApiKey.Trim());

        using HttpResponseMessage response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            string summary = ExtractOpenAiErrorSummary(body)
                ?? $"OpenAI request failed ({(int)response.StatusCode}).";
            target.Content += $"\n\n--- Retry Failed ({(int)response.StatusCode}) ---\n{summary}\n\n{body}";
            return;
        }

        string text = ExtractOpenAiResponseText(body);
        target.Content = string.IsNullOrWhiteSpace(text)
            ? target.Content + "\n\n(No response text received from retry request.)"
            : text;
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
    {
        if (TryExtractJsonErrorSummary(content, out string? summary))
            return summary;

        return null;
    }

    private static bool TryExtractJsonErrorSummary(string content, out string? summary)
    {
        summary = null;

        try
        {
            using var doc = JsonDocument.Parse(content);
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

    // ── Anthropic Messages API — Streaming SSE ───────────────────────────

    private async Task StreamAnthropicAsync(string prompt, ChatMessage target, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(AnthropicModel) ? "claude-sonnet-4-5" : AnthropicModel,
            ["max_tokens"] = MaxTokens,
            ["stream"] = true,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = prompt
                }
            }
        };

        if (AttachMcpServer && Uri.TryCreate(_mcpServerUrl.Trim(), UriKind.Absolute, out _))
        {
            var mcpServer = new JsonObject
            {
                ["name"] = "xrengine",
                ["url"] = _mcpServerUrl.Trim()
            };

            if (!string.IsNullOrWhiteSpace(_mcpAuthToken))
                mcpServer["authorization_token"] = _mcpAuthToken.Trim();

            payload["mcp_servers"] = new JsonArray(mcpServer);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", AnthropicApiKey.Trim());
        request.Headers.Add("anthropic-version", "2023-06-01");

        using HttpResponseMessage response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            target.Content = await response.Content.ReadAsStringAsync(ct);
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var sb = new StringBuilder();
        string? line;

        while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            string data = line[6..];
            if (data is "[DONE]")
                break;

            try
            {
                using var doc = JsonDocument.Parse(data);
                JsonElement root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl))
                    continue;

                string eventType = typeEl.GetString() ?? string.Empty;

                if (string.Equals(eventType, "content_block_delta", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("delta", out var deltaEl)
                    && deltaEl.TryGetProperty("text", out var textEl))
                {
                    string? text = textEl.GetString();
                    if (text is not null)
                    {
                        sb.Append(text);
                        target.Content = sb.ToString();
                    }
                }
                else if (string.Equals(eventType, "message_stop", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                else if (string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
                {
                    target.Content += $"\n\n--- SSE Error ---\n{data}";
                    break;
                }
            }
            catch (JsonException)
            {
                // Malformed SSE line — skip.
            }
        }
    }

    // ── OpenAI Realtime WebSocket — Already Streams ──────────────────────

    private async Task StreamOpenAiRealtimeAsync(string prompt, ChatMessage target, CancellationToken ct)
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
                break;
            }
            else if (string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
            {
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
        if (AttachMcpServer && !string.IsNullOrWhiteSpace(_mcpServerUrl))
            instructions += $" MCP server endpoint: {_mcpServerUrl.Trim()}";

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

    // ── MCP Tool Builder ─────────────────────────────────────────────────

    private bool TryBuildOpenAiMcpTool(out JsonObject mcpTool)
    {
        mcpTool = new JsonObject();
        if (!Uri.TryCreate(_mcpServerUrl.Trim(), UriKind.Absolute, out _))
            return false;

        mcpTool["type"] = "mcp";
        mcpTool["server_label"] = "xrengine";
        mcpTool["server_url"] = _mcpServerUrl.Trim();

        if (!string.IsNullOrWhiteSpace(_mcpAuthToken))
        {
            mcpTool["headers"] = new JsonObject
            {
                ["Authorization"] = $"Bearer {_mcpAuthToken.Trim()}"
            };
        }

        return true;
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
