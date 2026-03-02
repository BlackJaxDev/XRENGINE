using ImGuiNET;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor.Mcp;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
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
        ClaudeCode,
        Gemini,
        GitHubModels
    }

    private sealed class ToolCallEntry
    {
        public required string ToolName { get; init; }
        public string ArgsSummary { get; init; } = string.Empty;
        public string ResultSummary { get; set; } = string.Empty;
        /// <summary>
        /// A compact summary of the tool result that preserves IDs (node IDs, asset IDs, etc.).
        /// Used by <see cref="BuildConversationContextBlock"/> to keep ID references available
        /// across continuation turns so the model doesn't need to re-query.
        /// </summary>
        public string ContextResultSummary { get; set; } = string.Empty;
        public bool IsError { get; set; }
        public bool IsComplete { get; set; }
        /// <summary>Local file path returned by the tool (e.g. screenshot path), for UI rendering.</summary>
        public string? ResultFilePath { get; set; }
    }

    /// <summary>
    /// Result from an MCP tool call, carrying the text result and optional image data
    /// for multimodal feedback to the model.
    /// </summary>
    private readonly record struct ToolCallResult(
        string Text,
        string? ImageBase64 = null,
        string? ImagePath = null)
    {
        public bool HasImage => !string.IsNullOrEmpty(ImageBase64);
        public bool IsError => Text.StartsWith("[MCP Error]", StringComparison.Ordinal);
    }

    /// <summary>
    /// Represents a chronological segment of assistant output — either a block
    /// of text or a group of tool calls that occurred at that point in time.
    /// </summary>
    private sealed class ContentSegment
    {
        public enum SegmentKind { Text, ToolCallGroup }
        public SegmentKind Kind { get; init; }
        /// <summary>Text content (for Text segments).</summary>
        public string Text { get; set; } = string.Empty;
        /// <summary>Tool call entries (for ToolCallGroup segments).</summary>
        public List<ToolCallEntry> ToolCalls { get; } = [];
    }

    private sealed class ChatMessage
    {
        public required string Role { get; init; }
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public bool IsStreaming { get; set; }
        public List<ToolCallEntry> ToolCalls { get; } = [];
        public object ToolCallsSyncRoot { get; } = new();

        /// <summary>
        /// Ordered list of content segments for chronological rendering.
        /// Populated during streaming. If empty, falls back to Content + ToolCalls.
        /// </summary>
        public List<ContentSegment> Segments { get; } = [];
        public object SegmentsSyncRoot { get; } = new();
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
        "Claude Code (Anthropic)",
        "Gemini (Google)",
        "GitHub Models"
    ];

    private const string AssistantDoneMarker = "[[XRENGINE_ASSISTANT_DONE]]";
    private const string ContextSummaryStartMarker = "[[XRENGINE_CONTEXT_SUMMARY]]";
    private const string ContextSummaryEndMarker = "[[/XRENGINE_CONTEXT_SUMMARY]]";
    private const float ContextSummaryTriggerRatio = 0.80f;
    private const float AutoCameraViewFocusDurationSeconds = 0.35f;
    private const int DefaultOpenAiContextWindowTokens = 400_000;

    /// <summary>
    /// Well-known context window sizes (in tokens) for popular models.
    /// Used as a fallback when the provider API doesn't expose context metadata.
    /// Prefix-matched in order, so more specific entries should come first.
    /// Sources: developers.openai.com/api/docs/models, platform.claude.com/docs,
    ///          ai.google.dev/gemini-api/docs/models — verified 2026-03-01.
    /// </summary>
    private static readonly (string Prefix, int Tokens)[] KnownModelContextWindows =
    [
        // ── OpenAI — GPT-5.x family (400K context) ──────────────────────
        ("gpt-5.3-codex",       400_000),   // 128K max output
        ("gpt-5.2-codex",       400_000),   // 128K max output
        ("gpt-5.2-pro",         400_000),   // 128K max output
        ("gpt-5.2",             400_000),   // 128K max output
        ("gpt-5.1-codex-mini",  400_000),   // 128K max output
        ("gpt-5.1-codex-max",   400_000),   // 128K max output
        ("gpt-5.1-codex",       400_000),   // 128K max output
        ("gpt-5.1",             400_000),   // 128K max output
        ("gpt-5-codex",         400_000),   // 128K max output
        ("gpt-5-pro",           400_000),   // 272K max output
        ("gpt-5-mini",          400_000),   // 128K max output
        ("gpt-5-nano",          400_000),   // 128K max output
        ("gpt-5",               400_000),   // 128K max output

        // ── OpenAI — GPT-4.1 family (1M context) ────────────────────────
        ("gpt-4.1-mini",      1_047_576),   // 32K max output
        ("gpt-4.1-nano",      1_047_576),   // 32K max output
        ("gpt-4.1",           1_047_576),   // 32K max output

        // ── OpenAI — o-series reasoning ──────────────────────────────────
        ("o4-mini",             200_000),   // 100K max output
        ("o3-pro",              200_000),   // 100K max output
        ("o3-mini",             200_000),   // 100K max output
        ("o3",                  200_000),   // 100K max output
        ("o1-pro",              200_000),   // 100K max output
        ("o1-mini",             128_000),   // 65K max output
        ("o1",                  200_000),   // 100K max output

        // ── OpenAI — GPT-4o family ───────────────────────────────────────
        ("gpt-4o-mini",         128_000),   // 16K max output
        ("gpt-4o",              128_000),   // 16K max output

        // ── OpenAI — GPT-4 Turbo & base ─────────────────────────────────
        ("gpt-4-turbo",         128_000),   // 4K max output
        ("gpt-4-32k",            32_768),
        ("gpt-4",                 8_192),

        // ── OpenAI — GPT-3.5 ────────────────────────────────────────────
        ("gpt-3.5-turbo-16k",    16_385),
        ("gpt-3.5-turbo",        16_385),

        // ── OpenAI — Codex CLI (deprecated) ─────────────────────────────
        ("codex-mini",          200_000),

        // ── OpenAI — Open-weight ─────────────────────────────────────────
        ("gpt-oss-120b",        128_000),
        ("gpt-oss-20b",         128_000),

        // ── Anthropic — Claude 4.x family ────────────────────────────────
        ("claude-opus-4",       200_000),   // 128K max output; 1M beta
        ("claude-sonnet-4",     200_000),   // 64K max output; 1M beta
        ("claude-haiku-4",      200_000),   // 64K max output

        // ── Anthropic — Claude 3.7 ──────────────────────────────────────
        ("claude-3.7-sonnet",   200_000),

        // ── Anthropic — Claude 3.5 family ────────────────────────────────
        ("claude-3-5-sonnet",   200_000),   // 8K max output
        ("claude-3-5-haiku",    200_000),   // 8K max output
        ("claude-3.5-sonnet",   200_000),   // alt naming
        ("claude-3.5-haiku",    200_000),   // alt naming

        // ── Anthropic — Claude 3 family ──────────────────────────────────
        ("claude-3-opus",       200_000),   // 4K max output
        ("claude-3-sonnet",     200_000),   // 4K max output
        ("claude-3-haiku",      200_000),   // 4K max output

        // ── Anthropic — Claude 2 ────────────────────────────────────────
        ("claude-2",            200_000),

        // ── Google — Gemini 3 (preview) ──────────────────────────────────
        ("gemini-3",          1_048_576),

        // ── Google — Gemini 2.5 ──────────────────────────────────────────
        ("gemini-2.5-pro",    1_048_576),
        ("gemini-2.5-flash",  1_048_576),

        // ── Google — Gemini 2.0 ──────────────────────────────────────────
        ("gemini-2.0-flash",  1_048_576),

        // ── Google — Gemini 1.5 ──────────────────────────────────────────
        ("gemini-1.5-pro",    2_097_152),
        ("gemini-1.5-flash",  1_048_576),
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
    private static readonly Vector4 ColorGuidButton = new(0.20f, 0.38f, 0.58f, 1.00f);
    private static readonly Vector4 ColorGuidButtonHover = new(0.28f, 0.48f, 0.68f, 1.00f);
    private static readonly Vector4 ColorGuidButtonActive = new(0.32f, 0.52f, 0.72f, 1.00f);
    private static readonly Vector4 ColorGuidText = new(0.45f, 0.75f, 1.00f, 1.00f);
    private static readonly Vector4 ColorFilePath = new(0.45f, 0.78f, 0.92f, 1.00f);
    private static readonly Vector4 ColorFilePathHover = new(0.55f, 0.88f, 1.00f, 1.00f);

    /// <summary>
    /// Matches standard 8-4-4-4-12 GUID/UUID strings.
    /// </summary>
    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

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
    private readonly Dictionary<string, int> _openAiContextWindowByModel = new(StringComparer.OrdinalIgnoreCase);
    private int _openAiSelectedContextWindowTokens;
    private string _conversationSummary = string.Empty;
    private readonly List<McpUsageEntry> _mcpUsageHistory = [];
    /// <summary>
    /// Cache of loaded XRTexture2D objects for image preview thumbnails in tool call results.
    /// Keys are absolute file paths. Values are the loaded texture (or null if load failed).
    /// </summary>
    private readonly Dictionary<string, XRTexture2D?> _imagePreviewCache = new(StringComparer.OrdinalIgnoreCase);
    private string _lastPromptForContinue = string.Empty;
    private int _lastProviderIndexForContinue = -1;
    private bool _lastAttachMcpForContinue;
    private bool _canContinueAfterRepromptLimit;
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

    private string GeminiApiKey
    {
        get => Prefs?.McpAssistantGeminiApiKey ?? string.Empty;
        set { if (Prefs is { } p) p.McpAssistantGeminiApiKey = value; }
    }

    private string GeminiModel
    {
        get => Prefs?.McpAssistantGeminiModel ?? "gemini-2.5-pro";
        set { if (Prefs is { } p) p.McpAssistantGeminiModel = value; }
    }

    private string GitHubModelsToken
    {
        get => Prefs?.McpAssistantGitHubModelsToken ?? string.Empty;
        set { if (Prefs is { } p) p.McpAssistantGitHubModelsToken = value; }
    }

    private string GitHubModelsModel
    {
        get => Prefs?.McpAssistantGitHubModelsModel ?? "openai/gpt-4.1";
        set { if (Prefs is { } p) p.McpAssistantGitHubModelsModel = value; }
    }

    private int MaxTokens
    {
        get => Prefs?.McpAssistantMaxTokens ?? 4096;
        set { if (Prefs is { } p) p.McpAssistantMaxTokens = value; }
    }

    private int MaxAutoReprompts
    {
        get => Prefs?.McpAssistantMaxAutoReprompts ?? 3;
        set { if (Prefs is { } p) p.McpAssistantMaxAutoReprompts = value; }
    }

    private bool AutoSummarizeNearContextLimit
    {
        get => Prefs?.McpAssistantAutoSummarizeNearContextLimit ?? true;
        set { if (Prefs is { } p) p.McpAssistantAutoSummarizeNearContextLimit = value; }
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

    private bool AttachViewportScreenshot
    {
        get => Prefs?.McpAssistantAttachViewportScreenshot ?? false;
        set { if (Prefs is { } p) p.McpAssistantAttachViewportScreenshot = value; }
    }

    /// <summary>
    /// Base64-encoded PNG screenshot captured before sending a prompt.
    /// Set by <see cref="SendPromptAsync"/> / <see cref="ContinuePromptAsync"/> when the toggle is on,
    /// consumed and cleared by the provider streaming methods.
    /// </summary>
    private string? _pendingScreenshotBase64;

    private bool AutoScroll
    {
        get => Prefs?.McpAssistantAutoScroll ?? true;
        set { if (Prefs is { } p) p.McpAssistantAutoScroll = value; }
    }

    private bool AutoCameraView
    {
        get => Prefs?.McpAssistantAutoCameraView ?? true;
        set { if (Prefs is { } p) p.McpAssistantAutoCameraView = value; }
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
        RefreshSelectedModelContextWindow();
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
            {
                _history.Clear();
                _conversationSummary = string.Empty;
            }

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
                    _openAiContextWindowByModel.Clear();
                    _openAiSelectedContextWindowTokens = 0;
                    _openAiModelsStatus = "Not loaded";
                }

                string oaiModel = OpenAiModel;
                ImGui.SetNextItemWidth(220f);
                if (ImGui.InputText("Model##OAI", ref oaiModel, 128))
                {
                    OpenAiModel = oaiModel;
                    RefreshSelectedModelContextWindow();
                }
                if (ImGuiTextFieldHelper.DrawTextFieldContextMenu("ctx_ModelOAI", ref oaiModel))
                {
                    OpenAiModel = oaiModel;
                    RefreshSelectedModelContextWindow();
                }

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

            case ProviderType.Gemini:
            {
                string gemKey = GeminiApiKey;
                DrawSecretField("API Key##Gem", ref gemKey, 320f);
                GeminiApiKey = gemKey;

                string gemModel = GeminiModel;
                ImGui.SetNextItemWidth(220f);
                if (ImGui.InputText("Model##Gem", ref gemModel, 128))
                    GeminiModel = gemModel;
                if (ImGuiTextFieldHelper.DrawTextFieldContextMenu("ctx_ModelGem", ref gemModel))
                    GeminiModel = gemModel;
                break;
            }

            case ProviderType.GitHubModels:
            {
                string ghToken = GitHubModelsToken;
                DrawSecretField("PAT##GH", ref ghToken, 320f);
                GitHubModelsToken = ghToken;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("GitHub Personal Access Token with models:read scope.");

                string ghModel = GitHubModelsModel;
                ImGui.SetNextItemWidth(220f);
                if (ImGui.InputText("Model##GH", ref ghModel, 128))
                    GitHubModelsModel = ghModel;
                if (ImGuiTextFieldHelper.DrawTextFieldContextMenu("ctx_ModelGH", ref ghModel))
                    GitHubModelsModel = ghModel;

                ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f), "Note: GitHub Models has restrictive rate limits (50-150 req/day).");
                break;
            }
        }

        int maxTokens = MaxTokens;
        ImGui.SetNextItemWidth(140f);
        if (ImGui.InputInt("Max Tokens", ref maxTokens, 256, 1024))
            MaxTokens = Math.Clamp(maxTokens, 64, 128_000);

        int maxAutoReprompts = MaxAutoReprompts;
        ImGui.SetNextItemWidth(140f);
        if (ImGui.InputInt("Max Auto Re-prompts", ref maxAutoReprompts, 1, 2))
            MaxAutoReprompts = Math.Clamp(maxAutoReprompts, 0, 20);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Maximum automatic continue prompts sent for one user prompt when the assistant does not emit the done marker.");

        bool autoSummarizeNearLimit = AutoSummarizeNearContextLimit;
        if (ImGui.Checkbox("Auto summarize near context limit", ref autoSummarizeNearLimit))
            AutoSummarizeNearContextLimit = autoSummarizeNearLimit;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, the assistant asks the model to summarize prior context as the prompt approaches the selected model's context window.");

        bool autoCameraView = AutoCameraView;
        if (ImGui.Checkbox("Auto Camera View", ref autoCameraView))
            AutoCameraView = autoCameraView;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, the editor camera smoothly focuses relevant scene nodes after assistant scene-edit tool calls.");

        if (ImGui.Button("Load Keys from ENV"))
        {
            OpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? OpenAiApiKey;
            AnthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? AnthropicApiKey;
            GeminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? GeminiApiKey;
            GitHubModelsToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? GitHubModelsToken;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reads OPENAI_API_KEY, ANTHROPIC_API_KEY,\nGEMINI_API_KEY, and GITHUB_TOKEN\nenvironment variables.");

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
            ImGui.TextDisabled("Loading...");
        else
            ImGui.TextDisabled(_openAiModelsStatus);

        ImGui.TextDisabled($"Context Window: {FormatContextWindowLabel(_openAiSelectedContextWindowTokens)}");

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
                    {
                        OpenAiModel = model;
                        RefreshSelectedModelContextWindow();
                    }
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
        _openAiModelsStatus = "Loading...";

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

                if (TryExtractModelContextWindowTokens(item, out int contextWindowTokens))
                    _openAiContextWindowByModel[id] = contextWindowTokens;
            }

            List<string> chosen = textModels.Count > 0 ? textModels : allModels;
            chosen.Sort(StringComparer.OrdinalIgnoreCase);

            lock (_openAiModelLock)
                _openAiTextModels = chosen.ToArray();

            RefreshSelectedModelContextWindow();

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

    private static string FormatContextWindowLabel(int tokens)
        => tokens > 0 ? $"{tokens:n0} tokens" : "Unknown";

    private void RefreshSelectedModelContextWindow()
    {
        string model = OpenAiModel.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            _openAiSelectedContextWindowTokens = 0;
            return;
        }

        // 1) Check runtime cache (populated by a prior successful API fetch).
        if (_openAiContextWindowByModel.TryGetValue(model, out int knownTokens) && knownTokens > 0)
        {
            _openAiSelectedContextWindowTokens = knownTokens;
            return;
        }

        // 2) Fall back to the well-known table (prefix match, case-insensitive).
        if (TryLookupKnownContextWindow(model, out int wellKnown))
        {
            _openAiSelectedContextWindowTokens = wellKnown;
            _openAiContextWindowByModel[model] = wellKnown;
            // Still fire the async fetch — if the API returns a value it'll override.
        }
        else
        {
            _openAiSelectedContextWindowTokens = 0;
        }

        // 3) Try the provider API for an authoritative value (async, best-effort).
        _ = RefreshOpenAiModelContextWindowAsync(model);
    }

    /// <summary>
    /// Prefix-matches the model name against <see cref="KnownModelContextWindows"/>.
    /// </summary>
    private static bool TryLookupKnownContextWindow(string model, out int tokens)
    {
        foreach ((string prefix, int t) in KnownModelContextWindows)
        {
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                tokens = t;
                return true;
            }
        }
        tokens = 0;
        return false;
    }

    private async Task RefreshOpenAiModelContextWindowAsync(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return;

        string apiKey = OpenAiApiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        try
        {
            string url = $"https://api.openai.com/v1/models/{Uri.EscapeDataString(model)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using HttpResponseMessage response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return;

            string body = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(body);
            if (!TryExtractModelContextWindowTokens(doc.RootElement, out int contextWindowTokens) || contextWindowTokens <= 0)
                return;

            _openAiContextWindowByModel[model] = contextWindowTokens;
            if (string.Equals(model, OpenAiModel.Trim(), StringComparison.OrdinalIgnoreCase))
                _openAiSelectedContextWindowTokens = contextWindowTokens;
        }
        catch
        {
            // Best-effort metadata fetch.
        }
    }

    private static bool TryExtractModelContextWindowTokens(JsonElement modelElement, out int tokens)
    {
        tokens = 0;

        if (TryReadPositiveInt(modelElement, "context_window", out tokens)
            || TryReadPositiveInt(modelElement, "context_length", out tokens)
            || TryReadPositiveInt(modelElement, "max_context_tokens", out tokens)
            || TryReadPositiveInt(modelElement, "max_input_tokens", out tokens)
            || TryReadPositiveInt(modelElement, "input_token_limit", out tokens)
            || TryReadPositiveInt(modelElement, "max_prompt_tokens", out tokens))
            return true;

        if (modelElement.TryGetProperty("architecture", out JsonElement architecture)
            && architecture.ValueKind == JsonValueKind.Object)
        {
            if (TryReadPositiveInt(architecture, "input_tokens", out tokens)
                || TryReadPositiveInt(architecture, "max_input_tokens", out tokens)
                || TryReadPositiveInt(architecture, "context_window", out tokens))
                return true;
        }

        if (modelElement.TryGetProperty("capabilities", out JsonElement capabilities)
            && capabilities.ValueKind == JsonValueKind.Object)
        {
            if (TryReadPositiveInt(capabilities, "max_input_tokens", out tokens)
                || TryReadPositiveInt(capabilities, "context_window", out tokens)
                || TryReadPositiveInt(capabilities, "input_token_limit", out tokens))
                return true;
        }

        return false;
    }

    private static bool TryReadPositiveInt(JsonElement obj, string propertyName, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(propertyName, out JsonElement prop))
            return false;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int intValue) && intValue > 0)
        {
            value = intValue;
            return true;
        }

        if (prop.ValueKind == JsonValueKind.String
            && int.TryParse(prop.GetString(), out int parsed)
            && parsed > 0)
        {
            value = parsed;
            return true;
        }

        return false;
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

        // Render segments in chronological order when available.
        ContentSegment[] segments = SnapshotSegments(msg);
        if (segments.Length > 0)
        {
            int toolGroupIndex = 0;
            for (int s = 0; s < segments.Length; s++)
            {
                var seg = segments[s];
                if (seg.Kind == ContentSegment.SegmentKind.Text)
                {
                    string text = NormalizeForUiDisplay(GetDisplayContent(seg.Text));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        RenderRichContent(text);
                        ImGui.Spacing();
                        ImGui.Spacing();
                    }
                }
                else if (seg.Kind == ContentSegment.SegmentKind.ToolCallGroup)
                {
                    ToolCallEntry[] groupCalls;
                    lock (msg.SegmentsSyncRoot)
                        groupCalls = [.. seg.ToolCalls];

                    if (groupCalls.Length > 0)
                    {
                        DrawToolCallsSection(groupCalls, index * 100 + toolGroupIndex);
                        toolGroupIndex++;
                        ImGui.Spacing();
                    }
                }
            }
        }
        else
        {
            // Legacy fallback: text then tool calls.
            string displayContent = NormalizeForUiDisplay(GetDisplayContent(msg.Content));
            if (!string.IsNullOrWhiteSpace(displayContent))
                RenderRichContent(displayContent);

            ToolCallEntry[] toolCallsSnapshot = SnapshotToolCalls(msg);
            if (toolCallsSnapshot.Length > 0)
                DrawToolCallsSection(toolCallsSnapshot, index);
        }

        ImGui.Unindent(8f);
        ImGui.Spacing();
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
    private void DrawToolCallsSection(IReadOnlyList<ToolCallEntry> toolCalls, int messageIndex)
    {
        int total = toolCalls.Count;
        int completed = 0;
        int errors = 0;

        for (int i = 0; i < total; i++)
        {
            ToolCallEntry call = toolCalls[i];
            if (call.IsComplete)
                completed++;
            if (call.IsError)
                errors++;
        }

        bool allDone = completed == total;

        // Section header with count badge + status icon.
        string statusIcon = allDone ? (errors > 0 ? "!" : "\u2714") : "\u2026"; // ✔ or … or !
        string headerLabel = allDone
            ? $"{statusIcon} {total} tool call{(total != 1 ? "s" : "")} completed"
            : $"{statusIcon} Running {total - completed} of {total} tool call{(total != 1 ? "s" : "")}...";

        if (errors > 0)
            headerLabel += $" ({errors} failed)";

        ImGui.PushStyleColor(ImGuiCol.Header, ColorToolSection);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ColorToolSectionHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, ColorToolSectionHover);

        bool open = ImGui.TreeNodeEx($"{headerLabel}##tools_{messageIndex}",
            ImGuiTreeNodeFlags.SpanAvailWidth | (allDone ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen));

        ImGui.PopStyleColor(3);

        if (!open)
            return;

        for (int i = 0; i < total; i++)
        {
            ToolCallEntry call = toolCalls[i];

            // Status icon with visual indicators.
            if (!call.IsComplete)
            {
                int dots = ((int)(ImGui.GetTime() * 2.5)) % 4;
                string spinner = dots switch { 0 => "|", 1 => "/", 2 => "-", _ => "\\" };
                ImGui.TextColored(ColorBusy, spinner);
            }
            else if (call.IsError)
                ImGui.TextColored(ColorError, "x");
            else
                ImGui.TextColored(ColorDone, "\u2713"); // ✓

            ImGui.SameLine();

            // Tool name in a distinct color.
            ImGui.TextColored(new Vector4(0.65f, 0.50f, 0.90f, 1f), call.ToolName);

            // Build a concise one-line description.
            if (call.IsComplete && !string.IsNullOrEmpty(call.ResultSummary))
            {
                ImGui.SameLine();
                var resultColor = call.IsError ? ColorError : new Vector4(0.55f, 0.78f, 0.55f, 1f);
                bool resultFirst = false;
                RenderColoredTextWithGuidLinks($"  {call.ResultSummary}", resultColor, ref resultFirst);
            }
            else if (!string.IsNullOrEmpty(call.ArgsSummary))
            {
                ImGui.SameLine();
                bool argsFirst = false;
                RenderColoredTextWithGuidLinks($"  ({call.ArgsSummary})", ColorTimestamp, ref argsFirst);
            }

            // File path link + image preview for tool results.
            if (!string.IsNullOrEmpty(call.ResultFilePath))
            {
                DrawFilePathLink(call.ResultFilePath, messageIndex, i);

                // Image preview thumbnail.
                if (IsImageFilePath(call.ResultFilePath) && File.Exists(call.ResultFilePath))
                    DrawImagePreview(call.ResultFilePath, messageIndex, i);
            }
        }

        ImGui.TreePop();
        ImGui.Spacing();
    }

    /// <summary>
    /// Renders a clickable file path link that opens the file location in Explorer,
    /// plus an "Open" button that launches the file with the default application.
    /// </summary>
    private static void DrawFilePathLink(string filePath, int messageIndex, int callIndex)
    {
        string fileName = Path.GetFileName(filePath);
        ImGui.Indent(20f);

        // Clickable file name link.
        ImGui.PushStyleColor(ImGuiCol.Text, ColorFilePath);
        bool clicked = ImGui.Selectable($"{fileName}##fp_{messageIndex}_{callIndex}", false,
            ImGuiSelectableFlags.None, new Vector2(ImGui.CalcTextSize(fileName).X + 4f, 0));
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColorFilePathHover);
            ImGui.SetTooltip(filePath);
            ImGui.PopStyleColor();
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        if (clicked && File.Exists(filePath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
                {
                    UseShellExecute = true
                });
            }
            catch { /* Non-critical */ }
        }

        // "Open" button to launch with default application.
        ImGui.SameLine();
        if (ImGui.SmallButton($"Open##open_{messageIndex}_{callIndex}") && File.Exists(filePath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath)
                {
                    UseShellExecute = true
                });
            }
            catch { /* Non-critical */ }
        }

        ImGui.Unindent(20f);
    }

    /// <summary>
    /// Renders a collapsible image thumbnail preview for a tool result image.
    /// Uses a cached XRTexture2D loaded from the file path.
    /// </summary>
    private void DrawImagePreview(string imagePath, int messageIndex, int callIndex)
    {
        ImGui.Indent(20f);

        bool previewOpen = ImGui.TreeNodeEx($"Preview##imgpreview_{messageIndex}_{callIndex}",
            ImGuiTreeNodeFlags.None);

        if (previewOpen)
        {
            // Lazy-load and cache the texture.
            if (!_imagePreviewCache.TryGetValue(imagePath, out XRTexture2D? texture))
            {
                try
                {
                    texture = new XRTexture2D(imagePath);
                    _imagePreviewCache[imagePath] = texture;
                }
                catch
                {
                    _imagePreviewCache[imagePath] = null;
                    texture = null;
                }
            }

            if (texture is not null)
            {
                const float maxPreviewEdge = 256f;
                nint handle = nint.Zero;

                // Try to get the ImGui-compatible texture handle from the current renderer.
                if (Engine.IsRenderThread && AbstractRenderer.Current is { } renderer)
                {
                    if (renderer is VulkanRenderer vkRenderer)
                    {
                        IntPtr textureId = vkRenderer.RegisterImGuiTexture(texture);
                        if (textureId != IntPtr.Zero)
                            handle = (nint)textureId;
                    }
                    else if (renderer is OpenGLRenderer glRenderer)
                    {
                        var apiTexture = glRenderer.GenericToAPI<GLTexture2D>(texture);
                        if (apiTexture is not null)
                        {
                            uint binding = apiTexture.BindingId;
                            if (binding != 0)
                                handle = (nint)binding;
                        }
                    }
                }

                if (handle != nint.Zero)
                {
                    float w = Math.Max(texture.Width, 1f);
                    float h = Math.Max(texture.Height, 1f);
                    float scale = Math.Min(maxPreviewEdge / w, maxPreviewEdge / h);
                    if (scale > 1f) scale = 1f;
                    var displaySize = new Vector2(w * scale, h * scale);

                    ImGui.Image(handle, displaySize);
                    ImGui.TextDisabled($"{(int)w} x {(int)h}");
                }
                else
                {
                    ImGui.TextDisabled("(preview loading...)");
                }
            }
            else
            {
                ImGui.TextDisabled("(preview unavailable)");
            }

            ImGui.TreePop();
        }

        ImGui.Unindent(20f);
    }

    private static ToolCallEntry[] SnapshotToolCalls(ChatMessage message)
    {
        lock (message.ToolCallsSyncRoot)
            return [.. message.ToolCalls];
    }

    private static void AddToolCall(ChatMessage message, ToolCallEntry entry)
    {
        lock (message.ToolCallsSyncRoot)
            message.ToolCalls.Add(entry);
    }

    /// <summary>
    /// Snapshots the segment list for UI rendering.
    /// </summary>
    private static ContentSegment[] SnapshotSegments(ChatMessage message)
    {
        lock (message.SegmentsSyncRoot)
            return [.. message.Segments];
    }

    /// <summary>
    /// Gets the current text segment or creates one. A new text segment is started
    /// after a tool-call-group segment so text and tool calls alternate.
    /// </summary>
    private static ContentSegment GetOrCreateTextSegment(ChatMessage message)
    {
        lock (message.SegmentsSyncRoot)
        {
            if (message.Segments.Count > 0 && message.Segments[^1].Kind == ContentSegment.SegmentKind.Text)
                return message.Segments[^1];

            var seg = new ContentSegment { Kind = ContentSegment.SegmentKind.Text };
            message.Segments.Add(seg);
            return seg;
        }
    }

    /// <summary>
    /// Starts a new tool-call-group segment (or returns the current one if the last
    /// segment is already a tool group).
    /// </summary>
    private static ContentSegment GetOrCreateToolCallGroupSegment(ChatMessage message)
    {
        lock (message.SegmentsSyncRoot)
        {
            if (message.Segments.Count > 0 && message.Segments[^1].Kind == ContentSegment.SegmentKind.ToolCallGroup)
                return message.Segments[^1];

            var seg = new ContentSegment { Kind = ContentSegment.SegmentKind.ToolCallGroup };
            message.Segments.Add(seg);
            return seg;
        }
    }

    /// <summary>
    /// Adds a tool call entry to both the flat list (legacy/copy) and the current
    /// tool-call-group segment.
    /// </summary>
    private static void AddToolCallSegmented(ChatMessage message, ToolCallEntry entry)
    {
        lock (message.ToolCallsSyncRoot)
            message.ToolCalls.Add(entry);

        var group = GetOrCreateToolCallGroupSegment(message);
        lock (message.SegmentsSyncRoot)
            group.ToolCalls.Add(entry);
    }

    /// <summary>
    /// Syncs the current StringBuilder content to the current text segment.
    /// Call this whenever sb changes during streaming so the segment stays current.
    /// </summary>
    /// <param name="message">The chat message whose segments to update.</param>
    /// <param name="sb">The full accumulated StringBuilder for the entire response.</param>
    /// <param name="startOffset">
    /// The offset into <paramref name="sb"/> where the current round's text begins.
    /// Only text from this offset onward is written to the current text segment,
    /// preventing duplication when multiple tool-call rounds each produce model text.
    /// Pass 0 for single-round or non-looped streaming (e.g. Realtime).
    /// </param>
    private static void SyncTextSegment(ChatMessage message, StringBuilder sb, int startOffset = 0)
    {
        int length = sb.Length - startOffset;
        if (length <= 0)
            return;

        var seg = GetOrCreateTextSegment(message);
        seg.Text = sb.ToString(startOffset, length);
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

        string text = content;

        // Strip the done marker so it never shows in the UI.
        text = text.Replace(AssistantDoneMarker, string.Empty, StringComparison.Ordinal);

        // Remove trailing status markers like "[Executing tool calls...]".
        int bracketIdx = text.LastIndexOf('[');
        if (bracketIdx >= 0 && text.TrimEnd().EndsWith(']'))
        {
            string candidate = text[bracketIdx..];
            if (candidate.Contains("tool call", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("model response", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("Calling tool", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("tool result", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("Continuing", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("Sending", StringComparison.OrdinalIgnoreCase))
            {
                text = text[..bracketIdx].TrimEnd();
            }
        }

        return text.Trim();
    }

    private static string NormalizeForUiDisplay(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        string result = text
            .Replace("\u00A0", " ", StringComparison.Ordinal)
            .Replace("\u2013", "-", StringComparison.Ordinal)
            .Replace("\u2014", "-", StringComparison.Ordinal)
            .Replace("\u2018", "'", StringComparison.Ordinal)
            .Replace("\u2019", "'", StringComparison.Ordinal)
            .Replace("\u201C", "\"", StringComparison.Ordinal)
            .Replace("\u201D", "\"", StringComparison.Ordinal)
            .Replace("\u2026", "...", StringComparison.Ordinal)
            .Replace("\u2192", "->", StringComparison.Ordinal);

        // Fix missing space between sentences that got concatenated during
        // multi-round streaming (e.g. "frame it.Creating" -> "frame it. Creating").
        var sb = new StringBuilder(result.Length + 32);
        for (int i = 0; i < result.Length; i++)
        {
            char c = result[i];
            sb.Append(c);

            // After a sentence-ending punctuation followed immediately by an uppercase letter,
            // insert a space so the sentences are visually separated.
            if ((c == '.' || c == '!' || c == '?') && i + 1 < result.Length)
            {
                char next = result[i + 1];
                if (char.IsUpper(next))
                    sb.Append(' ');
            }
        }

        return sb.ToString();
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
            // If GUIDs are present, render with clickable links instead of plain text.
            if (ContainsGuid(text))
            {
                bool f = true;
                RenderTextWithGuidLinks(text, ref f);
                return;
            }

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
                    RenderTextWithGuidLinks(rest, ref first);
                break;
            }

            // Render plain text before the marker.
            if (next > pos)
            {
                string before = text[pos..next];
                RenderTextWithGuidLinks(before, ref first);
                pos = next;
            }

            if (isBacktick)
            {
                int close = text.IndexOf('`', pos + 1);
                if (close < 0)
                {
                    RenderTextWithGuidLinks(text[pos..], ref first);
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
                    RenderTextWithGuidLinks(text[pos..], ref first);
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
    /// If the code span is a single GUID, renders it as a clickable node-select button instead.
    /// </summary>
    private static void RenderCodeSpan(string code)
    {
        // If the entire code span is a GUID, render as a clickable link.
        string trimmed = code.Trim();
        if (trimmed.Length == 36 && Guid.TryParse(trimmed, out _))
        {
            bool unused = true;
            RenderGuidLink(trimmed, ref unused);
            return;
        }

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

    // ── GUID Link Rendering ──────────────────────────────────────────────

    /// <summary>
    /// Returns true if the text contains at least one GUID pattern.
    /// </summary>
    private static bool ContainsGuid(string text)
        => GuidPattern.IsMatch(text);

    /// <summary>
    /// Renders a GUID as a small clickable button that selects the corresponding
    /// scene node (or other XRObject) when clicked. Shows the object name as
    /// display text and the full GUID in a tooltip.
    /// </summary>
    private static void RenderGuidLink(string guidStr, ref bool first)
    {
        if (!Guid.TryParse(guidStr, out var guid))
        {
            if (!first) ImGui.SameLine(0, 0);
            ImGui.TextUnformatted(guidStr);
            first = false;
            return;
        }

        // Look up the object in the global cache.
        string displayName;
        XRObjectBase? obj = null;
        if (XRObjectBase.ObjectsCache.TryGetValue(guid, out var resolved) && !resolved.IsDestroyed)
        {
            obj = resolved;
            displayName = obj switch
            {
                SceneNode sn => sn.Name ?? "<unnamed>",
                _ => obj.GetType().Name
            };
        }
        else
        {
            // Object not in cache — show truncated GUID.
            displayName = guidStr[..8] + "...";
        }

        if (!first) ImGui.SameLine(0, 0);

        ImGui.PushStyleColor(ImGuiCol.Button, ColorGuidButton);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorGuidButtonHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorGuidButtonActive);
        ImGui.PushStyleColor(ImGuiCol.Text, ColorGuidText);

        // Use the full GUID as a unique ImGui ID.
        bool clicked = ImGui.SmallButton($"Select node: {displayName}##{guidStr}");

        ImGui.PopStyleColor(4);

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(guidStr);
            if (obj is SceneNode sn)
                ImGui.TextDisabled($"Click to select \"{sn.Name}\"");
            else if (obj is not null)
                ImGui.TextDisabled($"Type: {obj.GetType().Name}");
            else
                ImGui.TextDisabled("Object not found in cache");
            ImGui.EndTooltip();
        }

        if (clicked && obj is SceneNode clickedNode)
            Selection.SceneNodes = [clickedNode];

        first = false;
    }

    /// <summary>
    /// Renders a text segment inline, replacing any embedded GUID patterns with
    /// clickable <see cref="RenderGuidLink"/> buttons. Non-GUID text is rendered
    /// as plain <see cref="ImGui.TextUnformatted"/>.
    /// </summary>
    private static void RenderTextWithGuidLinks(string text, ref bool first)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var matches = GuidPattern.Matches(text);
        if (matches.Count == 0)
        {
            if (!first) ImGui.SameLine(0, 0);
            ImGui.TextUnformatted(text);
            first = false;
            return;
        }

        int pos = 0;
        foreach (Match match in matches)
        {
            // Render text before the GUID.
            if (match.Index > pos)
            {
                string before = text[pos..match.Index];
                if (!first) ImGui.SameLine(0, 0);
                ImGui.TextUnformatted(before);
                first = false;
            }

            // Render the GUID as a clickable link.
            RenderGuidLink(match.Value, ref first);

            pos = match.Index + match.Length;
        }

        // Render remaining text after the last GUID.
        if (pos < text.Length)
        {
            string after = text[pos..];
            if (!first) ImGui.SameLine(0, 0);
            ImGui.TextUnformatted(after);
            first = false;
        }
    }

    /// <summary>
    /// Like <see cref="RenderTextWithGuidLinks"/> but renders non-GUID text
    /// segments with <see cref="ImGui.TextColored"/> instead of plain text.
    /// GUIDs still render as clickable buttons via <see cref="RenderGuidLink"/>.
    /// </summary>
    private static void RenderColoredTextWithGuidLinks(string text, Vector4 color, ref bool first)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var matches = GuidPattern.Matches(text);
        if (matches.Count == 0)
        {
            if (!first) ImGui.SameLine(0, 0);
            ImGui.TextColored(color, text);
            first = false;
            return;
        }

        int pos = 0;
        foreach (Match match in matches)
        {
            if (match.Index > pos)
            {
                string before = text[pos..match.Index];
                if (!first) ImGui.SameLine(0, 0);
                ImGui.TextColored(color, before);
                first = false;
            }

            RenderGuidLink(match.Value, ref first);
            pos = match.Index + match.Length;
        }

        if (pos < text.Length)
        {
            string after = text[pos..];
            if (!first) ImGui.SameLine(0, 0);
            ImGui.TextColored(color, after);
            first = false;
        }
    }

    private static string StripGuidsForDisplay(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        string stripped = GuidPattern.Replace(text, "<node>");
        return Regex.Replace(stripped, @"\s+", " ").Trim();
    }

    private static void DrawInlineGuidButtonsFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        MatchCollection matches = GuidPattern.Matches(text);
        if (matches.Count == 0)
            return;

        var seen = new HashSet<Guid>();
        for (int i = 0; i < matches.Count; i++)
        {
            string value = matches[i].Value;
            if (!Guid.TryParse(value, out Guid guid) || !seen.Add(guid))
                continue;

            bool first = false;
            RenderGuidLink(value, ref first);
        }
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

        if (!_isBusy && _canContinueAfterRepromptLimit && !string.IsNullOrWhiteSpace(_lastPromptForContinue))
        {
            ImGui.SameLine();
            if (ImGui.Button("Continue (Refresh Auto Re-prompts)"))
            {
                if (_lastProviderIndexForContinue >= 0)
                    ProviderIndex = _lastProviderIndexForContinue;
                AttachMcpServer = _lastAttachMcpForContinue;
                _ = ContinuePromptAsync();
            }
        }

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

        // Screenshot toggle
        ImGui.SameLine();
        bool attachScreenshot = AttachViewportScreenshot;
        if (ImGui.Checkbox("Screenshot", ref attachScreenshot))
            AttachViewportScreenshot = attachScreenshot;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Capture and attach the current editor viewport\nscreenshot as visual context with your next message.");

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

        if (provider == ProviderType.Gemini && string.IsNullOrWhiteSpace(GeminiApiKey))
        {
            SetStatus("Gemini API key is required.", ColorError);
            return;
        }

        if (provider == ProviderType.GitHubModels && string.IsNullOrWhiteSpace(GitHubModelsToken))
        {
            SetStatus("GitHub PAT (models:read) is required.", ColorError);
            return;
        }

        _lastPromptForContinue = trimmedPrompt;
        _lastProviderIndexForContinue = ProviderIndex;
        _lastAttachMcpForContinue = AttachMcpServer;
        _canContinueAfterRepromptLimit = false;

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
            PromptPreview = trimmedPrompt.Length > 96 ? trimmedPrompt[..96] + "..." : trimmedPrompt,
            AttachRequested = attachRequested,
            Result = "Pending"
        };
        AddMcpUsageEntry(mcpUsage);

        if (attachRequested)
        {
            SetStatus("Validating MCP connection...", ColorBusy);
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

        // Capture viewport screenshot for visual context if the toggle is on.
        _pendingScreenshotBase64 = null;
        if (AttachViewportScreenshot)
        {
            SetStatus("Capturing screenshot\u2026", ColorBusy);
            try
            {
                using var screenshotTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _pendingScreenshotBase64 = await CaptureViewportScreenshotBase64Async(screenshotTimeout.Token);
            }
            catch
            {
                // Non-critical — just skip the screenshot.
            }
        }

        // Placeholder assistant message that will be filled by streaming.
        var assistantMsg = new ChatMessage { Role = "assistant", IsStreaming = true };
        _history.Add(assistantMsg);

        int maxAutoReprompts = Math.Clamp(MaxAutoReprompts, 0, 20);
        bool finishedByMarker = false;
        bool reachedRepromptLimit = false;

        SetStatus("Streaming\u2026", ColorBusy);
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            for (int attempt = 0; attempt <= maxAutoReprompts; attempt++)
            {
                bool requestSelfSummary = ShouldRequestSelfSummary(provider, trimmedPrompt, assistantMsg);
                string promptForAttempt = BuildProviderPromptEnvelope(trimmedPrompt, assistantMsg, attempt, requestSelfSummary);
                string priorContent = assistantMsg.Content;

                if (attempt == 0)
                    SetStatus("Streaming\u2026", ColorBusy);
                else
                    SetStatus($"Auto re-prompt {attempt}/{maxAutoReprompts}\u2026", ColorBusy);

                switch (provider)
                {
                    case ProviderType.Codex when UseRealtimeWebSocket:
                        mcpUsage.Note = "Realtime mode does not pass MCP tools array; only instructions include endpoint hint.";
                        await StreamOpenAiRealtimeAsync(promptForAttempt, assistantMsg, _cts.Token, mcpUsage);
                        break;
                    case ProviderType.Codex:
                        await StreamOpenAiResponsesAsync(promptForAttempt, assistantMsg, _cts.Token, mcpUsage);
                        break;
                    case ProviderType.ClaudeCode:
                        await StreamAnthropicAsync(promptForAttempt, assistantMsg, _cts.Token, mcpUsage);
                        break;
                    case ProviderType.Gemini:
                        await StreamChatCompletionsAsync(
                            promptForAttempt, assistantMsg, _cts.Token, mcpUsage,
                            provider: ProviderType.Gemini,
                            baseUrl: "https://generativelanguage.googleapis.com/v1beta/openai/",
                            model: string.IsNullOrWhiteSpace(GeminiModel) ? "gemini-2.5-pro" : GeminiModel.Trim(),
                            configureAuth: req => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GeminiApiKey.Trim()));
                        break;
                    case ProviderType.GitHubModels:
                        await StreamChatCompletionsAsync(
                            promptForAttempt, assistantMsg, _cts.Token, mcpUsage,
                            provider: ProviderType.GitHubModels,
                            baseUrl: "https://models.inference.ai.azure.com/",
                            model: string.IsNullOrWhiteSpace(GitHubModelsModel) ? "openai/gpt-4.1" : GitHubModelsModel.Trim(),
                            configureAuth: req => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GitHubModelsToken.Trim()));
                        break;
                }

                if (attempt > 0
                    && !string.IsNullOrWhiteSpace(priorContent)
                    && !string.IsNullOrWhiteSpace(assistantMsg.Content)
                    && !assistantMsg.Content.Contains(priorContent, StringComparison.Ordinal))
                {
                    assistantMsg.Content = $"{priorContent}\n\n{assistantMsg.Content}";
                }

                CaptureContextSummary(assistantMsg);
                if (TryStripDoneMarker(assistantMsg))
                {
                    if (IndicatesRemainingWork(assistantMsg.Content))
                    {
                        if (attempt >= maxAutoReprompts)
                        {
                            reachedRepromptLimit = true;
                            break;
                        }

                        SetStatus($"Model signaled done but response appears incomplete; auto re-prompting {attempt + 1}/{maxAutoReprompts}…", ColorBusy);
                        continue;
                    }

                    finishedByMarker = true;
                    break;
                }

                if (attempt >= maxAutoReprompts)
                {
                    reachedRepromptLimit = true;
                    break;
                }

                if (IsProviderErrorContent(assistantMsg.Content, out _))
                    break;

                if (string.IsNullOrWhiteSpace(assistantMsg.Content))
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
            else if (reachedRepromptLimit)
            {
                mcpUsage.Result = "Auto Re-prompt Limit";
                _canContinueAfterRepromptLimit = true;
                SetStatus("Auto re-prompt limit reached. Click Continue to run another auto-reprompt cycle.", ColorBusy);
            }
            else if (!finishedByMarker)
            {
                mcpUsage.Result = "Done (No Marker)";
                _canContinueAfterRepromptLimit = false;
                SetStatus("Stopped without done marker.", ColorBusy);
            }
            else
            {
                mcpUsage.Result = "Done";
                _canContinueAfterRepromptLimit = false;
                SetStatus("Done.", ColorDone);
            }
        }
        catch (OperationCanceledException)
        {
            assistantMsg.IsStreaming = false;
            if (string.IsNullOrWhiteSpace(assistantMsg.Content))
                assistantMsg.Content = "(Canceled.)";
            mcpUsage.Result = "Canceled";
            _canContinueAfterRepromptLimit = false;
            SetStatus("Canceled.", ColorError);
        }
        catch (Exception ex)
        {
            assistantMsg.IsStreaming = false;
            assistantMsg.Content += $"\n\n--- Error ---\n{ex}";
            mcpUsage.Result = "Error";
            mcpUsage.Note = ex.Message;
            _canContinueAfterRepromptLimit = false;
            SetStatus("Failed.", ColorError);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _isBusy = false;
        }
    }

    /// <summary>
    /// Continues the last assistant response in-place (no new user or assistant message)
    /// after auto-reprompt limit was reached and the user clicks "Continue".
    /// </summary>
    private async Task ContinuePromptAsync()
    {
        if (_isBusy)
            return;

        string trimmedPrompt = _lastPromptForContinue;
        if (string.IsNullOrWhiteSpace(trimmedPrompt))
            return;

        // Find the last assistant message to continue in.
        ChatMessage? assistantMsg = null;
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_history[i].Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                assistantMsg = _history[i];
                break;
            }
        }

        if (assistantMsg == null)
            return;

        ProviderType provider = (ProviderType)ProviderIndex;
        _canContinueAfterRepromptLimit = false;
        _isBusy = true;
        assistantMsg.IsStreaming = true;
        _scrollToBottom = true;

        bool attachRequested = AttachMcpServer;
        if (attachRequested)
            EnsureMcpServerAutoEnabledForAssistantMessage();

        var mcpUsage = new McpUsageEntry
        {
            Provider = provider.ToString(),
            PromptPreview = "(Continue) " + (trimmedPrompt.Length > 80 ? trimmedPrompt[..80] + "..." : trimmedPrompt),
            AttachRequested = attachRequested,
            Result = "Pending"
        };
        AddMcpUsageEntry(mcpUsage);

        if (attachRequested)
        {
            SetStatus("Validating MCP connection...", ColorBusy);
            using var preflightTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            (bool mcpReady, string reason) = await EnsureMcpServerAttachReadyAsync(preflightTimeout.Token);
            if (!mcpReady)
            {
                mcpUsage.Result = "MCP Unavailable";
                mcpUsage.Note = reason;
                SetStatus($"MCP unavailable: {reason}", ColorError);
                assistantMsg.IsStreaming = false;
                _isBusy = false;
                return;
            }
        }

        // Capture viewport screenshot for visual context if the toggle is on.
        _pendingScreenshotBase64 = null;
        if (AttachViewportScreenshot)
        {
            SetStatus("Capturing screenshot\u2026", ColorBusy);
            try
            {
                using var screenshotTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _pendingScreenshotBase64 = await CaptureViewportScreenshotBase64Async(screenshotTimeout.Token);
            }
            catch
            {
                // Non-critical — just skip the screenshot.
            }
        }

        int maxAutoReprompts = Math.Clamp(MaxAutoReprompts, 0, 20);
        bool finishedByMarker = false;
        bool reachedRepromptLimit = false;

        SetStatus("Continuing\u2026", ColorBusy);
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            for (int attempt = 0; attempt <= maxAutoReprompts; attempt++)
            {
                bool requestSelfSummary = ShouldRequestSelfSummary(provider, trimmedPrompt, assistantMsg);
                string promptForAttempt = BuildProviderPromptEnvelope(trimmedPrompt, assistantMsg, attempt, requestSelfSummary);
                string priorContent = assistantMsg.Content;

                if (attempt == 0)
                    SetStatus("Continuing\u2026", ColorBusy);
                else
                    SetStatus($"Auto re-prompt {attempt}/{maxAutoReprompts}\u2026", ColorBusy);

                switch (provider)
                {
                    case ProviderType.Codex when UseRealtimeWebSocket:
                        await StreamOpenAiRealtimeAsync(promptForAttempt, assistantMsg, _cts.Token, mcpUsage);
                        break;
                    case ProviderType.Codex:
                        await StreamOpenAiResponsesAsync(promptForAttempt, assistantMsg, _cts.Token, mcpUsage);
                        break;
                    case ProviderType.ClaudeCode:
                        await StreamAnthropicAsync(promptForAttempt, assistantMsg, _cts.Token, mcpUsage);
                        break;
                    case ProviderType.Gemini:
                        await StreamChatCompletionsAsync(
                            promptForAttempt, assistantMsg, _cts.Token, mcpUsage,
                            provider: ProviderType.Gemini,
                            baseUrl: "https://generativelanguage.googleapis.com/v1beta/openai/",
                            model: string.IsNullOrWhiteSpace(GeminiModel) ? "gemini-2.5-pro" : GeminiModel.Trim(),
                            configureAuth: req => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GeminiApiKey.Trim()));
                        break;
                    case ProviderType.GitHubModels:
                        await StreamChatCompletionsAsync(
                            promptForAttempt, assistantMsg, _cts.Token, mcpUsage,
                            provider: ProviderType.GitHubModels,
                            baseUrl: "https://models.inference.ai.azure.com/",
                            model: string.IsNullOrWhiteSpace(GitHubModelsModel) ? "openai/gpt-4.1" : GitHubModelsModel.Trim(),
                            configureAuth: req => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GitHubModelsToken.Trim()));
                        break;
                }

                if (!string.IsNullOrWhiteSpace(priorContent)
                    && !string.IsNullOrWhiteSpace(assistantMsg.Content)
                    && !assistantMsg.Content.Contains(priorContent, StringComparison.Ordinal))
                {
                    assistantMsg.Content = $"{priorContent}\n\n{assistantMsg.Content}";
                }

                CaptureContextSummary(assistantMsg);
                if (TryStripDoneMarker(assistantMsg))
                {
                    finishedByMarker = true;
                    break;
                }

                if (attempt >= maxAutoReprompts)
                {
                    reachedRepromptLimit = true;
                    break;
                }

                if (IsProviderErrorContent(assistantMsg.Content, out _))
                    break;

                if (string.IsNullOrWhiteSpace(assistantMsg.Content))
                    break;
            }

            assistantMsg.IsStreaming = false;

            if (IsProviderErrorContent(assistantMsg.Content, out string? providerErrorSummary))
            {
                mcpUsage.Result = "Failed";
                SetStatus(providerErrorSummary ?? "Failed.", ColorError);
            }
            else if (reachedRepromptLimit)
            {
                mcpUsage.Result = "Auto Re-prompt Limit";
                _canContinueAfterRepromptLimit = true;
                SetStatus("Auto re-prompt limit reached. Click Continue to run another cycle.", ColorBusy);
            }
            else if (!finishedByMarker)
            {
                mcpUsage.Result = "Done (No Marker)";
                SetStatus("Stopped without done marker.", ColorBusy);
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
            mcpUsage.Result = "Canceled";
            _canContinueAfterRepromptLimit = false;
            SetStatus("Canceled.", ColorError);
        }
        catch (Exception ex)
        {
            assistantMsg.IsStreaming = false;
            assistantMsg.Content += $"\n\n--- Error ---\n{ex}";
            mcpUsage.Result = "Error";
            mcpUsage.Note = ex.Message;
            _canContinueAfterRepromptLimit = false;
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

    private bool ShouldRequestSelfSummary(ProviderType provider, string currentPrompt, ChatMessage assistantMsg)
    {
        if (!AutoSummarizeNearContextLimit)
            return false;

        int contextWindowTokens = ResolveContextWindowTokens(provider);
        if (contextWindowTokens <= 0)
            return false;

        string context = BuildConversationContextBlock(assistantMsg, maxChars: 24_000);
        int estimatedTokens = EstimateTokens(context) + EstimateTokens(currentPrompt);
        return estimatedTokens >= (int)(contextWindowTokens * ContextSummaryTriggerRatio);
    }

    private int ResolveContextWindowTokens(ProviderType provider)
    {
        // For OpenAI Codex, use the dynamic lookup chain (API fetch → cache → well-known table).
        if (provider == ProviderType.Codex)
        {
            if (_openAiSelectedContextWindowTokens > 0)
                return _openAiSelectedContextWindowTokens;

            string model = OpenAiModel.Trim();
            if (!string.IsNullOrWhiteSpace(model)
                && _openAiContextWindowByModel.TryGetValue(model, out int mapped)
                && mapped > 0)
                return mapped;

            return DefaultOpenAiContextWindowTokens;
        }

        // For all other providers, look up the selected model in the well-known table.
        string selectedModel = provider switch
        {
            ProviderType.ClaudeCode => AnthropicModel.Trim(),
            ProviderType.Gemini => GeminiModel.Trim(),
            ProviderType.GitHubModels => GitHubModelsModel.Trim(),
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(selectedModel) && TryLookupKnownContextWindow(selectedModel, out int known))
            return known;

        // Sensible defaults per provider when the model isn't in the table.
        return provider switch
        {
            ProviderType.ClaudeCode => 200_000,
            ProviderType.Gemini => 1_048_576,
            ProviderType.GitHubModels => 128_000,
            _ => DefaultOpenAiContextWindowTokens
        };
    }

    private string BuildProviderPromptEnvelope(string userPrompt, ChatMessage assistantMsg, int autoRepromptIndex, bool requestSelfSummary)
    {
        int contextWindowTokens = ResolveContextWindowTokens((ProviderType)ProviderIndex);
        int contextCharsBudget = contextWindowTokens > 0
            ? Math.Clamp((int)(contextWindowTokens * 0.45f) * 4, 8_000, 48_000)
            : 12_000;

        string contextBlock = BuildConversationContextBlock(assistantMsg, contextCharsBudget);
        var sb = new StringBuilder();

        sb.AppendLine("Protocol requirements (must follow exactly):");
        sb.AppendLine("- Complete ALL parts of the user's request before finishing. Do NOT stop at an intermediate step.");
        sb.AppendLine("- If you have a multi-step plan, execute every step — do not list remaining steps and stop.");
        sb.AppendLine("- If a tool call fails, retry or use an alternative approach. Do not abandon the task.");
        sb.AppendLine("- Do NOT claim missing/denied editor or tool access unless a tool call in this conversation explicitly returned an access/permission error.");
        sb.AppendLine("- During tool use, include brief progress text; do not output only raw tool activity.");
        sb.AppendLine("- Avoid repeated read-only probes that return the same data. If no new information appears, stop probing and complete with best-effort results plus explicit blockers.");
        sb.AppendLine($"- Only when ALL requested work is done, output a completion footer with 'Completed Work' (summary of what was done) and 'Suggested Follow-ups' (optional ideas for the user — NOT remaining tasks from this request), then {AssistantDoneMarker} on the very last line.");
        sb.AppendLine("- If any work remains from the original request, do NOT output the footer or done marker — keep working.");
        sb.AppendLine("- Keep continuity with prior tool calls/results and avoid restarting work.");

        if (requestSelfSummary)
        {
            sb.AppendLine($"- Context is approaching limit. First output a concise summary wrapped in {ContextSummaryStartMarker}...{ContextSummaryEndMarker}, then continue with the task.");
        }

        if (contextWindowTokens > 0)
            sb.AppendLine($"- Selected model context window: {contextWindowTokens:n0} tokens.");

        if (autoRepromptIndex > 0)
        {
            sb.AppendLine($"- This is automatic continuation #{autoRepromptIndex}. You stopped before the work was fully complete.");
            sb.AppendLine("- Pick up exactly where you left off and finish ALL remaining work from the original request.");
            sb.AppendLine("- Do NOT re-explain what was already done. Do NOT ask the user what to do next. Just continue working.");
        }

        sb.AppendLine();
        sb.AppendLine("Conversation context:");
        sb.AppendLine(string.IsNullOrWhiteSpace(contextBlock) ? "(none)" : contextBlock);
        sb.AppendLine();
        sb.AppendLine("Current request:");
        sb.AppendLine(userPrompt);

        if (autoRepromptIndex > 0)
            sb.AppendLine("IMPORTANT: Complete all remaining work now. Do not stop until every part of the original request is fulfilled.");

        return sb.ToString();
    }

    private string BuildConversationContextBlock(ChatMessage activeAssistantMessage, int maxChars)
    {
        if (maxChars < 1024)
            maxChars = 1024;

        var chunks = new List<string>();
        int usedChars = 0;

        if (!string.IsNullOrWhiteSpace(_conversationSummary))
        {
            string summaryChunk = $"SUMMARY:\n{_conversationSummary.Trim()}";
            chunks.Add(summaryChunk);
            usedChars += summaryChunk.Length;
        }

        for (int i = _history.Count - 1; i >= 0; i--)
        {
            ChatMessage msg = _history[i];
            if (ReferenceEquals(msg, activeAssistantMessage) && string.IsNullOrWhiteSpace(msg.Content))
                continue;

            string cleaned = StripProtocolMarkers(msg.Content);
            if (string.IsNullOrWhiteSpace(cleaned))
                continue;

            string line = $"{msg.Role.ToUpperInvariant()}: {Truncate(cleaned.Replace('\n', ' '), 2200)}";
            ToolCallEntry[] toolCalls = SnapshotToolCalls(msg);
            if (toolCalls.Length > 0)
            {
                // Include tool names, arguments, and result summaries so the model
                // retains IDs (node GUIDs, asset IDs, etc.) across continuation turns.
                var toolParts = new List<string>(toolCalls.Length);
                foreach (var tc in toolCalls)
                {
                    var part = tc.ToolName;
                    if (!string.IsNullOrEmpty(tc.ArgsSummary))
                        part += $"({tc.ArgsSummary})";
                    if (!string.IsNullOrEmpty(tc.ContextResultSummary))
                        part += $" -> {tc.ContextResultSummary}";
                    else if (!string.IsNullOrEmpty(tc.ResultSummary))
                        part += $" -> {tc.ResultSummary}";
                    if (tc.IsError)
                        part += " [ERROR]";
                    toolParts.Add(part);
                }
                string toolSummary = string.Join("; ", toolParts);
                if (!string.IsNullOrWhiteSpace(toolSummary))
                    line += $" | tools: {Truncate(toolSummary, 1200)}";
            }

            if (usedChars + line.Length > maxChars)
                break;

            chunks.Add(line);
            usedChars += line.Length;
        }

        chunks.Reverse();
        return string.Join("\n", chunks);
    }

    private void CaptureContextSummary(ChatMessage assistantMsg)
    {
        string content = assistantMsg.Content;
        int start = content.IndexOf(ContextSummaryStartMarker, StringComparison.Ordinal);
        if (start < 0)
            return;

        int bodyStart = start + ContextSummaryStartMarker.Length;
        int end = content.IndexOf(ContextSummaryEndMarker, bodyStart, StringComparison.Ordinal);
        if (end < 0)
            return;

        string summary = content[bodyStart..end].Trim();
        if (!string.IsNullOrWhiteSpace(summary))
            _conversationSummary = summary;

        assistantMsg.Content = (content[..start] + content[(end + ContextSummaryEndMarker.Length)..]).Trim();
    }

    private static bool TryStripDoneMarker(ChatMessage assistantMsg)
    {
        int markerIndex = assistantMsg.Content.IndexOf(AssistantDoneMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return false;

        assistantMsg.Content = (assistantMsg.Content[..markerIndex]
            + assistantMsg.Content[(markerIndex + AssistantDoneMarker.Length)..]).Trim();
        return true;
    }

    private static bool IndicatesRemainingWork(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        string c = content.ToLowerInvariant();

        string[] unfinishedSignals =
        [
            "next i'll",
            "next i will",
            "i'll now",
            "i will now",
            "then i'll",
            "then i will",
            "still need to",
            "remaining work",
            "not finished",
            "not done",
            "i can now finish it by",
            "i can finish it by",
            "i still need to",
        ];

        for (int i = 0; i < unfinishedSignals.Length; i++)
        {
            if (c.Contains(unfinishedSignals[i], StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return Math.Max(1, text.Length / 4);
    }

    private static string StripProtocolMarkers(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        string cleaned = content.Replace(AssistantDoneMarker, string.Empty, StringComparison.Ordinal);
        cleaned = cleaned.Replace(ContextSummaryStartMarker, string.Empty, StringComparison.Ordinal);
        cleaned = cleaned.Replace(ContextSummaryEndMarker, string.Empty, StringComparison.Ordinal);
        return cleaned.Trim();
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

        // Local workspace tools are always available (file search, apply patch).
        JsonArray openAiFunctionTools = BuildLocalAssistantFunctionTools();

        // If MCP is enabled, fetch tools from local MCP server and convert to OpenAI function tools.
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

            AppendTools(openAiFunctionTools, ConvertMcpToolsToOpenAiFunctions(mcpTools));
            usage.McpPayloadIncluded = true;
            usage.ToolChoice = requestLikelyNeedsTools ? "required" : "auto";
        }

        string model = string.IsNullOrWhiteSpace(OpenAiModel) ? "gpt-4o" : OpenAiModel;

        // OpenAI-native hosted tools (Responses API) — model-dependent.
        JsonArray openAiHostedTools = BuildOpenAiHostedTools(model);

        // Build the conversation input — starts with just the user prompt, grows with tool call results.
        // If a viewport screenshot was captured, send multimodal content (text + image).
        JsonObject userMessage;
        string? screenshotBase64 = Interlocked.Exchange(ref _pendingScreenshotBase64, null);
        if (screenshotBase64 is not null)
        {
            userMessage = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "input_text", ["text"] = prompt },
                    new JsonObject
                    {
                        ["type"] = "input_image",
                        ["image_url"] = $"data:image/png;base64,{screenshotBase64}"
                    }
                }
            };
        }
        else
        {
            userMessage = new JsonObject { ["role"] = "user", ["content"] = prompt };
        }
        var conversationInput = new JsonArray { userMessage };

        string instructions = BuildSystemInstructions(ProviderType.Codex, requestLikelyNeedsTools, attachMcp: AttachMcpServer, keepCameraOnWorkingArea: AutoCameraView);

        const int maxToolRounds = 10;
        var sb = new StringBuilder();
        var toolLog = new StringBuilder(); // Human-readable log of tool calls shown in the response.
        var seenEventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int sseLineCount = 0;

        for (int round = 0; round < maxToolRounds; round++)
        {
            // Track where this round's text starts in the shared StringBuilder
            // so that SyncTextSegment only writes this round's text to the current segment.
            int sbRoundStart = sb.Length;
            bool forceTextFinalRound = round == maxToolRounds - 1;

            var payload = new JsonObject
            {
                ["model"] = model,
                ["input"] = JsonNode.Parse(conversationInput.ToJsonString()),
                ["stream"] = true,
                ["instructions"] = instructions
            };

            var allTools = new JsonArray();
            AppendTools(allTools, openAiFunctionTools);
            AppendTools(allTools, openAiHostedTools);

            if (allTools.Count > 0)
            {
                payload["tools"] = JsonNode.Parse(allTools.ToJsonString());
                if (forceTextFinalRound)
                    payload["tool_choice"] = "none";
                // Only require function tool usage on first round when mutation is likely.
                else if (round == 0 && requestLikelyNeedsTools && AttachMcpServer && openAiFunctionTools.Count > 0)
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
                SyncTextSegment(target, sb, sbRoundStart);
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
                            SyncTextSegment(target, sb, sbRoundStart);
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
                        SyncTextSegment(target, sb, sbRoundStart);
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

                        if (TryExtractGeneratedImagePath(root, out string? imagePath))
                        {
                            if (sb.Length > 0)
                                sb.Append("\n\n");
                            sb.Append($"[Image generated: {imagePath}]");
                        }

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

            if (forceTextFinalRound)
            {
                target.Content = BuildAssistantContent(sb, toolLog, "Tool-call round limit reached. Stopping further tool calls.");
                break;
            }

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
                AddToolCallSegmented(target, tcEntry);

                var toolResult = await ExecuteMcpToolCallAsync(call.Name, call.Arguments.ToString(), ct);
                usage.McpEventCount++;

                tcEntry.ResultSummary = SummarizeToolResult(toolResult.Text);
                tcEntry.ContextResultSummary = SummarizeToolResultForContext(toolResult.Text);
                tcEntry.IsError = toolResult.IsError;
                tcEntry.ResultFilePath = toolResult.ImagePath;
                tcEntry.IsComplete = true;

                if (toolResult.IsError)
                    Debug.LogWarning($"MCP tool call '{call.Name}' failed: {toolResult.Text}");

                AppendToolCallLog(toolLog, call.Name, call.Arguments.ToString(), toolResult.Text);
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
                    ["output"] = toolResult.Text
                });

                // If the tool produced an image (e.g. screenshot), attach it as visual context.
                if (toolResult.HasImage)
                {
                    conversationInput.Add(new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "input_image",
                                ["image_url"] = $"data:image/png;base64,{toolResult.ImageBase64}"
                            }
                        }
                    });
                }
            }

            // Continue the loop — next round will send conversation with tool results.
            target.Content = BuildAssistantContent(sb, toolLog, "Waiting for model response...");
        }

        if (sb.Length > 0)
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
        JsonArray anthropicTools = ConvertOpenAiFunctionsToAnthropicTools(BuildLocalAssistantFunctionTools());

        // If MCP is enabled, fetch tools from local MCP server and convert to Anthropic format.
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

            AppendTools(anthropicTools, ConvertMcpToolsToAnthropicTools(mcpTools));
            usage.McpPayloadIncluded = true;
            usage.ToolChoice = "auto";
        }

        bool requestLikelyNeedsTools = IsLikelySceneMutationPrompt(prompt);
        usage.RequireToolUse = requestLikelyNeedsTools;

        string model = string.IsNullOrWhiteSpace(AnthropicModel) ? "claude-sonnet-4-5" : AnthropicModel;
        string instructions = BuildSystemInstructions(ProviderType.ClaudeCode, requestLikelyNeedsTools, attachMcp: AttachMcpServer, keepCameraOnWorkingArea: AutoCameraView);

        // Build conversation messages — starts with user prompt, grows with tool results.
        // If a viewport screenshot was captured, send multimodal content (image + text).
        JsonNode userContent;
        string? screenshotBase64 = Interlocked.Exchange(ref _pendingScreenshotBase64, null);
        if (screenshotBase64 is not null)
        {
            userContent = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = "image/png",
                        ["data"] = screenshotBase64
                    }
                },
                new JsonObject { ["type"] = "text", ["text"] = prompt }
            };
        }
        else
        {
            userContent = JsonValue.Create(prompt)!;
        }
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = userContent }
        };

        const int maxToolRounds = 10;
        var sb = new StringBuilder();
        var toolLog = new StringBuilder(); // Human-readable log of tool calls shown in the response.
        var seenEventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int sseLineCount = 0;

        for (int round = 0; round < maxToolRounds; round++)
        {
            // Track where this round's text starts in the shared StringBuilder
            // so that SyncTextSegment only writes this round's text to the current segment.
            int sbRoundStart = sb.Length;

            var payload = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = MaxTokens,
                ["stream"] = true,
                ["system"] = instructions,
                ["messages"] = JsonNode.Parse(messages.ToJsonString())
            };

            if (anthropicTools.Count > 0)
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
                                    SyncTextSegment(target, sb, sbRoundStart);
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
                                SyncTextSegment(target, sb, sbRoundStart);
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
                AddToolCallSegmented(target, tcEntry);

                var toolResult = await ExecuteMcpToolCallAsync(toolName, inputStr, ct);
                usage.McpEventCount++;

                tcEntry.ResultSummary = SummarizeToolResult(toolResult.Text);
                tcEntry.ContextResultSummary = SummarizeToolResultForContext(toolResult.Text);
                tcEntry.IsError = toolResult.IsError;
                tcEntry.ResultFilePath = toolResult.ImagePath;
                tcEntry.IsComplete = true;

                if (toolResult.IsError)
                    Debug.LogWarning($"MCP tool call '{toolName}' failed: {toolResult.Text}");

                AppendToolCallLog(toolLog, toolName, inputStr, toolResult.Text);
                target.Content = BuildAssistantContent(sb, toolLog, "Executing tool calls...");

                // Add tool_result block for user message, with optional image content.
                if (toolResult.HasImage)
                {
                    toolResultContent.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolId,
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = toolResult.Text
                            },
                            new JsonObject
                            {
                                ["type"] = "image",
                                ["source"] = new JsonObject
                                {
                                    ["type"] = "base64",
                                    ["media_type"] = "image/png",
                                    ["data"] = toolResult.ImageBase64
                                }
                            }
                        }
                    });
                }
                else
                {
                    toolResultContent.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolId,
                        ["content"] = toolResult.Text
                    });
                }
            }

            // Add assistant turn and user turn with tool results
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistantContent });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResultContent });

            target.Content = BuildAssistantContent(sb, toolLog, "Waiting for model response...");
        }

        if (sb.Length > 0)
            target.Content = BuildAssistantContent(sb, toolLog);
        else if (string.IsNullOrWhiteSpace(target.Content))
        {
            string events = seenEventTypes.Count > 0
                ? string.Join(", ", seenEventTypes)
                : "none";
            target.Content = $"(No response content received from the API. SSE lines: {sseLineCount}, event types seen: {events})";
        }
    }

    // ── OpenAI-Compatible Chat Completions API — Streaming SSE with Tool-Use Loop ─
    // Used by Gemini (Google) and GitHub Models, which expose OpenAI-compatible
    // chat/completions endpoints with standard SSE streaming and function calling.

    private async Task StreamChatCompletionsAsync(
        string prompt,
        ChatMessage target,
        CancellationToken ct,
        McpUsageEntry usage,
        ProviderType provider,
        string baseUrl,
        string model,
        Action<HttpRequestMessage> configureAuth)
    {
        bool requestLikelyNeedsTools = IsLikelySceneMutationPrompt(prompt);
        usage.RequireToolUse = requestLikelyNeedsTools;

        // Local workspace tools are always available (file search, apply patch).
        JsonArray functionTools = BuildLocalAssistantFunctionTools();

        // If MCP is enabled, fetch tools as OpenAI function tools.
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

            AppendTools(functionTools, ConvertMcpToolsToOpenAiFunctions(mcpTools));
            usage.McpPayloadIncluded = true;
            usage.ToolChoice = requestLikelyNeedsTools ? "required" : "auto";
        }

        string instructions = BuildSystemInstructions(provider, requestLikelyNeedsTools, attachMcp: AttachMcpServer, keepCameraOnWorkingArea: AutoCameraView);

        // Build conversation messages — starts with system + user prompt, grows with tool results.
        // If a viewport screenshot was captured, send multimodal content (text + image_url).
        JsonNode userContent;
        string? screenshotBase64 = Interlocked.Exchange(ref _pendingScreenshotBase64, null);
        if (screenshotBase64 is not null)
        {
            userContent = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = prompt },
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = $"data:image/png;base64,{screenshotBase64}"
                    }
                }
            };
        }
        else
        {
            userContent = JsonValue.Create(prompt)!;
        }
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = instructions },
            new JsonObject { ["role"] = "user", ["content"] = userContent }
        };

        const int maxToolRounds = 10;
        var sb = new StringBuilder();
        var toolLog = new StringBuilder();
        var seenEventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int sseLineCount = 0;

        for (int round = 0; round < maxToolRounds; round++)
        {
            // Track where this round's text starts in the shared StringBuilder
            // so that SyncTextSegment only writes this round's text to the current segment.
            int sbRoundStart = sb.Length;
            bool forceTextFinalRound = round == maxToolRounds - 1;

            var payload = new JsonObject
            {
                ["model"] = model,
                ["messages"] = JsonNode.Parse(messages.ToJsonString()),
                ["stream"] = true,
                ["max_tokens"] = MaxTokens
            };

            if (functionTools.Count > 0)
            {
                payload["tools"] = JsonNode.Parse(functionTools.ToJsonString());
                if (forceTextFinalRound)
                    payload["tool_choice"] = "none";
                else if (round == 0 && requestLikelyNeedsTools && AttachMcpServer)
                    payload["tool_choice"] = "required";
            }

            string url = baseUrl.TrimEnd('/') + "/chat/completions";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            configureAuth(request);

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

            // Stream SSE — Chat Completions returns delta chunks with choices[0].delta.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            // Track pending tool calls by index within the choices[0] delta.
            var pendingCalls = new List<PendingFunctionCall>();
            var callsByIndex = new Dictionary<int, PendingFunctionCall>();
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

                    // Chat Completions SSE shape: { choices: [{ delta: { content, tool_calls }, finish_reason }] }
                    if (!root.TryGetProperty("choices", out var choicesEl)
                        || choicesEl.GetArrayLength() == 0)
                        continue;

                    JsonElement choice = choicesEl[0];

                    if (!choice.TryGetProperty("delta", out var deltaEl))
                        continue;

                    // Text content delta
                    if (deltaEl.TryGetProperty("content", out var contentEl)
                        && contentEl.ValueKind == JsonValueKind.String)
                    {
                        string? text = contentEl.GetString();
                        if (text is not null)
                        {
                            sb.Append(text);
                            target.Content = BuildAssistantContent(sb, toolLog);
                            SyncTextSegment(target, sb, sbRoundStart);
                        }
                    }

                    // Tool call deltas (streamed incrementally)
                    if (deltaEl.TryGetProperty("tool_calls", out var tcArrayEl)
                        && tcArrayEl.ValueKind == JsonValueKind.Array)
                    {
                        usage.ToolEventCount++;
                        foreach (JsonElement tcEl in tcArrayEl.EnumerateArray())
                        {
                            int idx = tcEl.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : 0;

                            if (!callsByIndex.TryGetValue(idx, out var pending))
                            {
                                string? callId = tcEl.TryGetProperty("id", out var cidEl) ? cidEl.GetString() : $"call_{idx}";
                                string? name = null;
                                if (tcEl.TryGetProperty("function", out var fnEl)
                                    && fnEl.TryGetProperty("name", out var nameEl))
                                    name = nameEl.GetString();

                                pending = new PendingFunctionCall { CallId = callId ?? $"call_{idx}", Name = name ?? string.Empty };
                                callsByIndex[idx] = pending;
                                pendingCalls.Add(pending);
                            }

                            // Accumulate function name if streamed in parts
                            if (tcEl.TryGetProperty("function", out var fnEl2))
                            {
                                if (fnEl2.TryGetProperty("arguments", out var argsEl)
                                    && argsEl.ValueKind == JsonValueKind.String)
                                {
                                    pending.Arguments.Append(argsEl.GetString());
                                }
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Malformed SSE line — skip.
                }
            }

            // If no function calls, we're done.
            if (pendingCalls.Count == 0)
                break;

            if (forceTextFinalRound)
            {
                target.Content = BuildAssistantContent(sb, toolLog, "Tool-call round limit reached. Stopping further tool calls.");
                break;
            }

            // Execute function calls locally against the MCP server.
            string toolNames = string.Join(", ", pendingCalls.Select(c => c.Name));
            target.Content = sb.Length > 0
                ? sb + $"\n\n[Executing {pendingCalls.Count} tool(s): {toolNames}...]"
                : $"[Executing {pendingCalls.Count} tool(s): {toolNames}...]";
            usage.ToolEventCount += pendingCalls.Count;

            // Add assistant message with tool_calls to conversation
            var assistantToolCallsArray = new JsonArray();
            foreach (var call in pendingCalls)
            {
                assistantToolCallsArray.Add(new JsonObject
                {
                    ["id"] = call.CallId,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments.ToString()
                    }
                });
            }
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = assistantToolCallsArray
            });

            foreach (var call in pendingCalls)
            {
                var tcEntry = new ToolCallEntry
                {
                    ToolName = FormatToolName(call.Name),
                    ArgsSummary = SummarizeToolArguments(call.Name, call.Arguments.ToString()),
                };
                AddToolCallSegmented(target, tcEntry);

                var toolResult = await ExecuteMcpToolCallAsync(call.Name, call.Arguments.ToString(), ct);
                usage.McpEventCount++;

                tcEntry.ResultSummary = SummarizeToolResult(toolResult.Text);
                tcEntry.ContextResultSummary = SummarizeToolResultForContext(toolResult.Text);
                tcEntry.IsError = toolResult.IsError;
                tcEntry.ResultFilePath = toolResult.ImagePath;
                tcEntry.IsComplete = true;

                if (toolResult.IsError)
                    Debug.LogWarning($"MCP tool call '{call.Name}' failed: {toolResult.Text}");

                AppendToolCallLog(toolLog, call.Name, call.Arguments.ToString(), toolResult.Text);
                target.Content = BuildAssistantContent(sb, toolLog, "Executing tool calls...");

                // Append tool result to conversation, with optional image for multimodal context.
                if (toolResult.HasImage)
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call.CallId,
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = toolResult.Text
                            },
                            new JsonObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JsonObject
                                {
                                    ["url"] = $"data:image/png;base64,{toolResult.ImageBase64}"
                                }
                            }
                        }
                    });
                }
                else
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call.CallId,
                        ["content"] = toolResult.Text
                    });
                }
            }

            // Continue the loop — next round will send conversation with tool results.
            target.Content = BuildAssistantContent(sb, toolLog, "Waiting for model response...");
        }

        if (sb.Length > 0)
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
        var toolLog = new StringBuilder();
        var pendingCallsByOutputIndex = new Dictionary<int, PendingFunctionCall>();
        var pendingCallsByItemId = new Dictionary<string, PendingFunctionCall>(StringComparer.Ordinal);

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
                target.Content = BuildAssistantContent(sb, toolLog);
                SyncTextSegment(target, sb);
            }
            else if (string.Equals(eventType, "response.output_item.added", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("output_index", out var outputIndexEl)
                && outputIndexEl.TryGetInt32(out int outputIndex)
                && root.TryGetProperty("item", out var itemEl)
                && itemEl.TryGetProperty("type", out var itemTypeEl)
                && string.Equals(itemTypeEl.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
            {
                string itemId = itemEl.TryGetProperty("id", out var itemIdEl) ? itemIdEl.GetString() ?? string.Empty : string.Empty;
                string callId = itemEl.TryGetProperty("call_id", out var callIdEl) ? callIdEl.GetString() ?? string.Empty : string.Empty;
                string name = itemEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;

                var pending = new PendingFunctionCall
                {
                    CallId = callId,
                    Name = name,
                };

                pendingCallsByOutputIndex[outputIndex] = pending;
                if (!string.IsNullOrWhiteSpace(itemId))
                    pendingCallsByItemId[itemId] = pending;

                target.Content = BuildAssistantContent(sb, toolLog, $"Calling tool: {name}...");
                usage.ToolEventCount++;
            }
            else if (string.Equals(eventType, "response.function_call_arguments.delta", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("output_index", out var outputIndexDeltaEl)
                && outputIndexDeltaEl.TryGetInt32(out int outputIndexDelta)
                && root.TryGetProperty("delta", out var argDeltaEl)
                && argDeltaEl.ValueKind == JsonValueKind.String
                && pendingCallsByOutputIndex.TryGetValue(outputIndexDelta, out var pendingDeltaCall))
            {
                pendingDeltaCall.Arguments.Append(argDeltaEl.GetString());
            }
            else if (string.Equals(eventType, "response.function_call_arguments.done", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("output_index", out var outputIndexDoneEl)
                && outputIndexDoneEl.TryGetInt32(out int outputIndexDone)
                && root.TryGetProperty("arguments", out var argsDoneEl)
                && argsDoneEl.ValueKind == JsonValueKind.String
                && pendingCallsByOutputIndex.TryGetValue(outputIndexDone, out var pendingDoneCall))
            {
                pendingDoneCall.Arguments.Clear();
                pendingDoneCall.Arguments.Append(argsDoneEl.GetString());
            }
            else if (string.Equals(eventType, "response.output_item.done", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("item", out var doneItemEl)
                && doneItemEl.TryGetProperty("type", out var doneItemTypeEl)
                && string.Equals(doneItemTypeEl.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
            {
                string? doneItemId = doneItemEl.TryGetProperty("id", out var doneItemIdEl) ? doneItemIdEl.GetString() : null;
                PendingFunctionCall? pendingDone = null;

                if (!string.IsNullOrWhiteSpace(doneItemId)
                    && pendingCallsByItemId.TryGetValue(doneItemId!, out var callByItem))
                {
                    pendingDone = callByItem;
                    pendingCallsByItemId.Remove(doneItemId!);
                }

                if (pendingDone is null
                    && root.TryGetProperty("output_index", out var outputIndexDoneItemEl)
                    && outputIndexDoneItemEl.TryGetInt32(out int outputIndexDoneItem)
                    && pendingCallsByOutputIndex.TryGetValue(outputIndexDoneItem, out var callByOutputIndex))
                {
                    pendingDone = callByOutputIndex;
                    pendingCallsByOutputIndex.Remove(outputIndexDoneItem);
                }

                if (pendingDone is null)
                    continue;

                if (doneItemEl.TryGetProperty("arguments", out var finalArgsEl) && finalArgsEl.ValueKind == JsonValueKind.String)
                {
                    pendingDone.Arguments.Clear();
                    pendingDone.Arguments.Append(finalArgsEl.GetString());
                }

                var tcEntry = new ToolCallEntry
                {
                    ToolName = FormatToolName(pendingDone.Name),
                    ArgsSummary = SummarizeToolArguments(pendingDone.Name, pendingDone.Arguments.ToString()),
                };
                AddToolCallSegmented(target, tcEntry);

                var toolResult = await ExecuteRealtimeFunctionCallAsync(pendingDone, ct);
                usage.McpEventCount++;

                tcEntry.ResultSummary = SummarizeToolResult(toolResult.FunctionOutput);
                tcEntry.ContextResultSummary = SummarizeToolResultForContext(toolResult.FunctionOutput);
                tcEntry.IsError = toolResult.IsError;
                tcEntry.IsComplete = true;

                if (toolResult.IsError)
                    Debug.LogWarning($"MCP tool call '{pendingDone.Name}' failed: {toolResult.FunctionOutput}");

                AppendToolCallLog(toolLog, pendingDone.Name, pendingDone.Arguments.ToString(), toolResult.FunctionOutput);
                target.Content = BuildAssistantContent(sb, toolLog, "Sending tool result...");

                await SendWsJsonAsync(socket, BuildRealtimeFunctionCallOutputItem(pendingDone.CallId, toolResult.FunctionOutput), ct);

                if (!string.IsNullOrWhiteSpace(toolResult.ImageDataUrl))
                {
                    await SendWsJsonAsync(socket, BuildRealtimeScreenshotContextMessage(toolResult), ct);
                }

                await SendWsJsonAsync(socket, BuildRealtimeResponseCreate(), ct);
                target.Content = BuildAssistantContent(sb, toolLog, "Continuing model response...");
            }
            else if (string.Equals(eventType, "response.completed", StringComparison.OrdinalIgnoreCase))
            {
                usage.Result = "Done";
                break;
            }
            else if (string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
            {
                usage.Result = "Failed";
                target.Content = BuildAssistantContent(sb, toolLog, json);
                break;
            }
        }

        if (sb.Length > 0)
            target.Content = BuildAssistantContent(sb, toolLog);

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
        string instructions = BuildSystemInstructions(
            ProviderType.Codex,
            requireToolUse: false,
            attachMcp: AttachMcpServer,
            keepCameraOnWorkingArea: AutoCameraView,
            isRealtimeSession: true);

        return new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["instructions"] = instructions,
                ["modalities"] = new JsonArray("text"),
                ["tool_choice"] = "auto",
                ["tools"] = BuildRealtimeFunctionTools()
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

    private static JsonArray BuildRealtimeFunctionTools() =>
    [
        new JsonObject
        {
            ["type"] = "function",
            ["name"] = "request_view_screenshot",
            ["description"] = "Capture a screenshot from the current editor view or a camera and return it for visual reasoning.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["camera_node_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional camera scene node GUID."
                    },
                    ["camera_name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional camera scene node name when ID is unknown."
                    },
                    ["window_index"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Optional editor window index. Defaults to 0."
                    },
                    ["viewport_index"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Optional viewport index within the window. Defaults to 0."
                    },
                    ["note"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional reason for the screenshot request."
                    }
                },
                ["additionalProperties"] = false
            }
        }
    ];

    private static JsonObject BuildRealtimeFunctionCallOutputItem(string callId, string output) => new()
    {
        ["type"] = "conversation.item.create",
        ["item"] = new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = callId,
            ["output"] = output
        }
    };

    private static JsonObject BuildRealtimeScreenshotContextMessage(RealtimeFunctionResult result)
    {
        string text = string.IsNullOrWhiteSpace(result.ContextText)
            ? "Requested screenshot attached."
            : result.ContextText;

        return new JsonObject
        {
            ["type"] = "conversation.item.create",
            ["item"] = new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "input_text", ["text"] = text },
                    new JsonObject { ["type"] = "input_image", ["image_url"] = result.ImageDataUrl }
                }
            }
        };
    }

    private async Task<RealtimeFunctionResult> ExecuteRealtimeFunctionCallAsync(PendingFunctionCall call, CancellationToken ct)
    {
        if (!string.Equals(call.Name, "request_view_screenshot", StringComparison.Ordinal))
        {
            return new RealtimeFunctionResult
            {
                IsError = true,
                FunctionOutput = JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = $"Unsupported realtime function '{call.Name}'."
                })
            };
        }

        try
        {
            JsonElement args = default;
            bool hasArgs = false;
            string argsRaw = call.Arguments.ToString();
            if (!string.IsNullOrWhiteSpace(argsRaw))
            {
                using var argsDoc = JsonDocument.Parse(argsRaw);
                args = argsDoc.RootElement.Clone();
                hasArgs = true;
            }

            string? cameraNodeId = hasArgs && args.TryGetProperty("camera_node_id", out var cameraNodeIdEl) && cameraNodeIdEl.ValueKind == JsonValueKind.String
                ? cameraNodeIdEl.GetString()
                : null;

            string? cameraName = hasArgs && args.TryGetProperty("camera_name", out var cameraNameEl) && cameraNameEl.ValueKind == JsonValueKind.String
                ? cameraNameEl.GetString()
                : null;

            int windowIndex = hasArgs && args.TryGetProperty("window_index", out var windowIndexEl) && windowIndexEl.TryGetInt32(out int parsedWindow)
                ? parsedWindow
                : 0;

            int viewportIndex = hasArgs && args.TryGetProperty("viewport_index", out var viewportIndexEl) && viewportIndexEl.TryGetInt32(out int parsedViewport)
                ? parsedViewport
                : 0;

            string note = hasArgs && args.TryGetProperty("note", out var noteEl) && noteEl.ValueKind == JsonValueKind.String
                ? noteEl.GetString() ?? string.Empty
                : string.Empty;

            var capture = await CaptureRealtimeScreenshotAsync(cameraNodeId, cameraName, windowIndex, viewportIndex, ct);
            byte[] bytes = await File.ReadAllBytesAsync(capture.Path, ct);
            string dataUrl = "data:image/png;base64," + Convert.ToBase64String(bytes);

            string functionOutput = JsonSerializer.Serialize(new
            {
                ok = true,
                path = capture.Path,
                camera_node_id = capture.CameraNodeId,
                camera_name = capture.CameraName,
                window_index = capture.WindowIndex,
                viewport_index = capture.ViewportIndex,
                note
            });

            string contextText = string.IsNullOrWhiteSpace(capture.CameraName)
                ? "Requested screenshot from the current viewport."
                : $"Requested screenshot from camera '{capture.CameraName}'.";

            return new RealtimeFunctionResult
            {
                FunctionOutput = functionOutput,
                ImageDataUrl = dataUrl,
                ContextText = contextText,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RealtimeFunctionResult
            {
                IsError = true,
                FunctionOutput = JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "Screenshot request timed out."
                })
            };
        }
        catch (Exception ex)
        {
            return new RealtimeFunctionResult
            {
                IsError = true,
                FunctionOutput = JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = ex.Message
                })
            };
        }
    }

    /// <summary>
    /// Captures the primary editor viewport and returns the image as a base64-encoded PNG string.
    /// Returns <c>null</c> if no viewport is available or the capture fails.
    /// </summary>
    private static async Task<string?> CaptureViewportScreenshotBase64Async(CancellationToken ct)
    {
        var viewport = Engine.Windows.FirstOrDefault()?.Viewports.FirstOrDefault();
        if (viewport is null)
            return null;

        var window = viewport.Window ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(viewport));
        if (window is null)
            return null;

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? deferredHandler = null;

        static void BeginCapture(AbstractRenderer renderer, XRViewport viewport, TaskCompletionSource<byte[]> tcs)
        {
            renderer.GetScreenshotAsync(viewport.Region, false, (img, _) =>
            {
                if (img is null)
                {
                    tcs.TrySetException(new InvalidOperationException("Screenshot capture returned null."));
                    return;
                }

                try
                {
                    img.Flip();
                    tcs.TrySetResult(img.ToByteArray(ImageMagick.MagickFormat.Png));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }

        void ScheduleCaptureOnRenderThread()
        {
            if (AbstractRenderer.Current is not null)
            {
                BeginCapture(AbstractRenderer.Current, viewport, tcs);
                return;
            }

            int captureStarted = 0;
            deferredHandler = () =>
            {
                var renderer = AbstractRenderer.Current;
                if (renderer is null)
                    return;

                if (Interlocked.CompareExchange(ref captureStarted, 1, 0) != 0)
                    return;

                window.RenderViewportsCallback -= deferredHandler;
                BeginCapture(renderer, viewport, tcs);
            };

            window.RenderViewportsCallback += deferredHandler;
        }

        if (Engine.IsRenderThread)
        {
            ScheduleCaptureOnRenderThread();
        }
        else
        {
            Engine.InvokeOnMainThread(ScheduleCaptureOnRenderThread, "MCP Assistant: Capture viewport screenshot", executeNowIfAlreadyMainThread: true);
        }

        using var reg = ct.Register(() =>
        {
            if (deferredHandler is not null)
            {
                var cancelWindow = viewport.Window ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(viewport));
                cancelWindow?.RenderViewportsCallback -= deferredHandler;
            }

            tcs.TrySetCanceled(ct);
        });

        try
        {
            byte[] bytes = await tcs.Task;
            return Convert.ToBase64String(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<RealtimeScreenshotCapture> CaptureRealtimeScreenshotAsync(string? cameraNodeId, string? cameraName, int windowIndex, int viewportIndex, CancellationToken ct)
    {
        XRWorldInstance? world = McpWorldResolver.TryGetActiveWorldInstance();
        XRViewport? viewport = ResolveRealtimeViewport(world, cameraNodeId, cameraName, windowIndex, viewportIndex, out SceneNode? cameraNode);
        if (viewport is null)
            throw new InvalidOperationException("No viewport found to capture.");

        string folder = Path.Combine(Environment.CurrentDirectory, "McpCaptures");
        string fileName = $"RealtimeScreenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
        string path = Path.Combine(folder, fileName);

        Directory.CreateDirectory(folder);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? deferredHandler = null;

        var window = viewport.Window ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(viewport));
        if (window is null)
            throw new InvalidOperationException("No window found to capture from.");

        static void BeginCapture(AbstractRenderer renderer, XRViewport viewport, string path, TaskCompletionSource<string> tcs)
        {
            renderer.GetScreenshotAsync(viewport.Region, false, (img, _) =>
            {
                if (img is null)
                {
                    tcs.TrySetException(new InvalidOperationException("Screenshot capture returned null."));
                    return;
                }

                try
                {
                    img.Flip();
                    img.Write(path);
                    tcs.TrySetResult(path);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }

        void ScheduleCaptureOnRenderThread()
        {
            if (AbstractRenderer.Current is not null)
            {
                BeginCapture(AbstractRenderer.Current, viewport, path, tcs);
                return;
            }

            int captureStarted = 0;
            deferredHandler = () =>
            {
                var renderer = AbstractRenderer.Current;
                if (renderer is null)
                    return;

                if (Interlocked.CompareExchange(ref captureStarted, 1, 0) != 0)
                    return;

                window.RenderViewportsCallback -= deferredHandler;
                BeginCapture(renderer, viewport, path, tcs);
            };

            window.RenderViewportsCallback += deferredHandler;
        }

        if (Engine.IsRenderThread)
        {
            ScheduleCaptureOnRenderThread();
        }
        else
        {
            Engine.InvokeOnMainThread(ScheduleCaptureOnRenderThread, "Realtime: Capture screenshot", executeNowIfAlreadyMainThread: true);
        }

        using var reg = ct.Register(() =>
        {
            if (deferredHandler is not null)
            {
                var cancelWindow = viewport.Window ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(viewport));
                cancelWindow?.RenderViewportsCallback -= deferredHandler;
            }

            tcs.TrySetCanceled(ct);
        });

        string savedPath = await tcs.Task;
        return new RealtimeScreenshotCapture
        {
            Path = savedPath,
            CameraNodeId = cameraNode?.ID.ToString(),
            CameraName = cameraNode?.Name,
            WindowIndex = ResolveWindowIndex(window),
            ViewportIndex = window.Viewports.IndexOf(viewport)
        };
    }

    private static int ResolveWindowIndex(XRWindow window)
    {
        int index = 0;
        foreach (var entry in Engine.Windows)
        {
            if (ReferenceEquals(entry, window))
                return index;
            index++;
        }

        return -1;
    }

    private static XRViewport? ResolveRealtimeViewport(
        XRWorldInstance? world,
        string? cameraNodeId,
        string? cameraName,
        int windowIndex,
        int viewportIndex,
        out SceneNode? cameraNode)
    {
        cameraNode = null;

        CameraComponent? camera = null;
        if (world is not null)
            camera = ResolveCameraComponent(world, cameraNodeId, cameraName, out cameraNode);

        if (camera is not null)
        {
            foreach (var activeViewport in Engine.EnumerateActiveViewports())
            {
                if (ReferenceEquals(activeViewport.CameraComponent, camera))
                    return activeViewport;
            }
        }

        if (windowIndex < 0 || windowIndex >= Engine.Windows.Count)
            return Engine.Windows.FirstOrDefault()?.Viewports.FirstOrDefault();

        var window = Engine.Windows[windowIndex];
        if (window.Viewports.Count == 0)
            return null;

        if (viewportIndex < 0 || viewportIndex >= window.Viewports.Count)
            return window.Viewports.FirstOrDefault();

        return window.Viewports[viewportIndex];
    }

    private static CameraComponent? ResolveCameraComponent(XRWorldInstance world, string? cameraNodeId, string? cameraName, out SceneNode? cameraNode)
    {
        cameraNode = null;

        if (!string.IsNullOrWhiteSpace(cameraNodeId)
            && Guid.TryParse(cameraNodeId, out var guid)
            && XRObjectBase.ObjectsCache.TryGetValue(guid, out var obj)
            && obj is SceneNode idNode
            && idNode.World == world)
        {
            var camera = idNode.GetComponent<CameraComponent>();
            if (camera is not null)
            {
                cameraNode = idNode;
                return camera;
            }
        }

        if (!string.IsNullOrWhiteSpace(cameraName))
        {
            foreach (var node in EnumerateWorldNodes(world))
            {
                if (!string.Equals(node.Name, cameraName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var camera = node.GetComponent<CameraComponent>();
                if (camera is not null)
                {
                    cameraNode = node;
                    return camera;
                }
            }
        }

        return null;
    }

    private static IEnumerable<SceneNode> EnumerateWorldNodes(XRWorldInstance world)
    {
        foreach (var root in world.RootNodes)
        {
            if (root is null)
                continue;

            foreach (var node in EnumerateNodeAndDescendants(root))
                yield return node;
        }
    }

    private static IEnumerable<SceneNode> EnumerateNodeAndDescendants(SceneNode root)
    {
        yield return root;

        foreach (var childTransform in root.Transform.Children)
        {
            var childNode = childTransform.SceneNode;
            if (childNode is null)
                continue;

            foreach (var child in EnumerateNodeAndDescendants(childNode))
                yield return child;
        }
    }

    /// <summary>
    /// Builds the system / instructions prompt shared by every provider.
    /// Provider-specific wording (e.g. Anthropic's tool_use block format) is
    /// appended only when <paramref name="provider"/> indicates it is relevant.
    /// </summary>
    private static string BuildSystemInstructions(
        ProviderType provider,
        bool requireToolUse,
        bool attachMcp,
        bool keepCameraOnWorkingArea,
        bool isRealtimeSession = false)
    {
        var sb = new StringBuilder(512);

        // ── Identity & tone ──────────────────────────────────────────────
        sb.Append("You are an assistant embedded in the XREngine editor, a Windows-first C# XR engine. ");
        sb.Append("Be concise, actionable, and accurate. ");
        sb.Append("When describing scene changes, state what was done rather than how to do it.");
        sb.Append(' ');
        sb.Append(BuildMachineContextInstruction());

        // ── Completion protocol ─────────────────────────────────────────
        sb.Append(" CRITICAL: You must fully complete every part of the user's request before finishing.");
        sb.Append(" Do NOT stop at intermediate milestones. If you planned multiple steps, execute ALL of them — do not describe remaining work and stop.");
        sb.Append(" If a tool call fails, retry with corrected parameters or try an alternative approach rather than giving up.");
        sb.Append(" While using tools, provide short natural-language progress updates; avoid long stretches of tool-only output.");
        sb.Append(" Do not repeatedly call the same read-only tools with unchanged arguments unless the previous result indicates state changed.");
        sb.Append(" When a tool returns IDs (node_id, component_id, asset_id), use those IDs immediately in subsequent mutation calls — do not re-query for the same information.");
        sb.Append(" For property edits, use schema-first behavior: discover writable members with get_component_schema/get_component_snapshot/get_component_property first, then mutate using exact discovered member names.");
        sb.Append(" Do not guess shader parameter semantics or material member names from intuition alone.");
        sb.Append(" Every mutation must be followed by verification read-back (get_component_property, get_component_snapshot, or screenshot for visual tasks) before claiming success.");
        sb.Append(" If additional tool calls are no longer adding new information, stop tool-calling and produce the best possible completion with explicit blockers.");
        sb.Append($" Only when ALL requested work is done, end your message with a brief completion footer: 'Completed Work' summarizing what was done, then 'Issues' listing any tool call failures, errors, or problems encountered during execution (include the tool name and a one-line description of each issue; omit this section if there were none), then 'Suggested Follow-ups' with optional ideas the user might explore next — these are NOT remaining tasks from the current request. End with {AssistantDoneMarker} on the very last line.");
        sb.Append(" If ANY work from the original request remains, do NOT output the completion footer or done marker — keep working.");

        // ── Built-in hosted tools (OpenAI Responses API only) ────────────
        if (provider == ProviderType.Codex && !isRealtimeSession)
            sb.Append(" You have access to web search. Use it when the user's question benefits from up-to-date information.");

        // ── Realtime-specific visual context hint ────────────────────────
        if (isRealtimeSession)
        {
            sb.Append(" If you need current visual context, call request_view_screenshot while reasoning.");
            sb.Append(" You may pass camera_node_id to target a specific camera node, or camera_name to target by scene node name.");
        }

        // ── MCP scene tools ──────────────────────────────────────────────
        if (attachMcp)
        {
            sb.Append(" You have function tools that interact with the running XREngine editor.");
            sb.Append(" These tools let you list, create, modify, and delete scene nodes, components, transforms, and other editor objects.");

            // ── Key tool workflow recipes ────────────────────────────────
            // These give the model concrete tool chains instead of leaving it to guess.
            sb.AppendLine();
            sb.Append(" TOOL WORKFLOW CHEAT SHEET — use these exact tool chains:");
            sb.Append(" [Create a colored shape] create_primitive_shape(shape_type, name, color, size, parent_id) — this creates a node with mesh+material in one call.");
            sb.Append(" [Change an existing node's color] 1) list_components(node_id), 2) get_component_schema(component_type) + get_component_snapshot(node_id, component_id=...) to discover writable members and material references, 3) mutate using set_component_property/set_component_properties with discovered member names OR assign_component_asset_property(node_id, 'Material', ...) after create_material_asset(...), 4) verify with get_component_property/get_component_snapshot and then capture_viewport_screenshot().");
            sb.Append(" [Move/scale/rotate a node] set_transform(node_id, translation_x/y/z, scale_x/y/z) or rotate_transform(node_id, axis_x/y/z, angle_degrees).");
            sb.Append(" [Add a light] create a new scene node via batch_create_nodes or create_primitive_shape, then add_component_to_node(node_id, 'PointLightComponent' or 'DirectionalLightComponent' or 'SpotLightComponent'), then set_component_property to configure it.");
            sb.Append(" [Verify visual result] capture_viewport_screenshot() — ALWAYS do this after visual changes.");
            sb.Append(" [Frame camera on work] focus_node_in_view(node_id) or set_editor_camera_view(...).");

            // ── Scene completeness guidance ──────────────────────────────
            sb.Append(" IMPORTANT scene rules: Mesh/shape components MUST have a Material assigned to be visible.");
            sb.Append(" create_primitive_shape auto-assigns a default lit material, but if you add mesh components manually via add_component, you must also assign a material via assign_component_asset_property or set_object_property.");
            sb.Append(" When creating materials, use create_material_asset with a material_kind (lit_color, unlit_color_forward, deferred_color) and color.");
            sb.Append(" CRITICAL: After creating a material with create_material_asset, you MUST assign it to the target mesh component using assign_component_asset_property(node_id, 'Material', asset_id=<the material ID returned by create_material_asset>, component_type=<the mesh component type, e.g. 'BoxMeshComponent' or 'ShapeMeshComponent'>). A material that is created but not assigned to a component has NO visual effect.");
            sb.Append(" For shader/material edits, prefer changing strongly-typed material/component members discovered via schema; avoid assuming universal names like 'Color' unless confirmed by read tools.");
            sb.Append(" After completing any visual/scene work, ALWAYS call capture_viewport_screenshot to verify the result is actually visible and correct. Do NOT assume success from tool return values alone — visually confirm.");
            sb.Append(" If the screenshot shows objects are not visible, diagnose (missing material, wrong position, camera not aimed at objects) and fix before completing.");

            if (requireToolUse)
                sb.Append(" For scene-edit requests, call the appropriate tools to perform the change first, then briefly report what was changed.");
            else
                sb.Append(" Use the scene tools when they materially improve correctness or when the user explicitly asks for a scene operation.");

            if (keepCameraOnWorkingArea)
            {
                sb.Append(" Auto Camera View is enabled for this user.");
                sb.Append(" Keep the editor camera on your current working area while performing scene edits by calling camera view tools when context shifts (for example focus_node_in_view or set_editor_camera_view).");
            }
        }

        // ── Provider-specific notes ──────────────────────────────────────
        if (provider == ProviderType.ClaudeCode)
        {
            // Anthropic returns tool results inside tool_use content blocks;
            // remind the model to keep tool-result summaries brief.
            sb.Append(" After receiving tool results, summarize them concisely for the user rather than echoing raw JSON.");
        }

        return sb.ToString();
    }

    private static string BuildMachineContextInstruction()
    {
        string osFamily = OperatingSystem.IsWindows()
            ? "Windows"
            : OperatingSystem.IsLinux()
                ? "Linux"
                : OperatingSystem.IsMacOS()
                    ? "macOS"
                    : "Unknown OS";

        string osDescription = RuntimeInformation.OSDescription.Trim();
        string architecture = RuntimeInformation.ProcessArchitecture.ToString();
        string runtimeVersion = Environment.Version.ToString();
        string localDate = DateTime.Now.ToString("yyyy-MM-dd");

        return $"Machine context: host OS is {osFamily} ({osDescription}), process architecture is {architecture}, local date is {localDate}, and runtime is .NET {runtimeVersion}. Prefer Windows-style paths/commands unless the user explicitly asks for another platform.";
    }

    /// <summary>
    /// OpenAI model prefixes known to support the <c>image_generation</c> hosted tool.
    /// Codex/reasoning models (gpt-5-codex, o-series, codex-mini, etc.) do NOT support it.
    /// </summary>
    private static readonly string[] ModelsWithImageGeneration =
    [
        "gpt-4o",
        "gpt-4.1",
        "gpt-4-turbo",
        "gpt-3.5",
    ];

    /// <summary>
    /// Returns true when the given OpenAI model name is known to support
    /// the hosted <c>image_generation</c> tool on the Responses API.
    /// </summary>
    private static bool ModelSupportsImageGeneration(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        foreach (string prefix in ModelsWithImageGeneration)
        {
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static JsonArray BuildOpenAiHostedTools(string model)
    {
        var tools = new JsonArray
        {
            new JsonObject { ["type"] = "web_search_preview" }
        };

        if (ModelSupportsImageGeneration(model))
            tools.Add(new JsonObject { ["type"] = "image_generation" });

        return tools;
    }

    private static JsonArray BuildLocalAssistantFunctionTools()
        =>
        [
            new JsonObject
            {
                ["type"] = "function",
                ["name"] = "file_search",
                ["description"] = "Search workspace files for text. Returns matching paths and line snippets.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Case-insensitive text to search for in files."
                        },
                        ["pattern"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional glob filter like *.cs or *.md."
                        },
                        ["root"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional root folder for the search. Defaults to current workspace root."
                        },
                        ["max_results"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Maximum number of file matches to return (1-200)."
                        }
                    },
                    ["required"] = new JsonArray("query"),
                    ["additionalProperties"] = false
                }
            },
            new JsonObject
            {
                ["type"] = "function",
                ["name"] = "apply_patch",
                ["description"] = "Apply a unified diff patch to files in the workspace using git apply.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["patch"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Unified diff patch text (git-style)."
                        },
                        ["root"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional working directory where git apply runs."
                        },
                        ["dry_run"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "If true, validates patch with git apply --check without applying."
                        }
                    },
                    ["required"] = new JsonArray("patch"),
                    ["additionalProperties"] = false
                }
            }
        ];

    private static void AppendTools(JsonArray target, JsonArray? source)
    {
        if (source is null)
            return;

        foreach (JsonNode? tool in source)
        {
            if (tool is null)
                continue;
            target.Add(JsonNode.Parse(tool.ToJsonString()));
        }
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

    private static JsonArray ConvertOpenAiFunctionsToAnthropicTools(JsonArray openAiTools)
    {
        var result = new JsonArray();
        foreach (JsonNode? tool in openAiTools)
        {
            if (tool is not JsonObject toolObj)
                continue;

            string? type = toolObj["type"]?.GetValue<string>();
            if (!string.Equals(type, "function", StringComparison.OrdinalIgnoreCase))
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

            if (toolObj["parameters"] is JsonObject schema)
                anthropicTool["input_schema"] = JsonNode.Parse(schema.ToJsonString());
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
    /// Builds the final assistant content string from model text and an optional trailing status.
    /// Tool call details are displayed separately in the collapsible tool-calls section.
    /// </summary>
    private static string BuildAssistantContent(StringBuilder modelText, StringBuilder toolLog, string? statusSuffix = null)
    {
        var result = new StringBuilder();

        if (modelText.Length > 0)
            result.Append(modelText);

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

        string cleaned = StripInlineJsonSuffix(result);
        return Truncate(cleaned, 100);
    }

    /// <summary>
    /// Produces a compact summary of a tool result suitable for the conversation context block.
    /// Unlike <see cref="SummarizeToolResult"/> (which is for UI display), this method preserves
    /// IDs (node GUIDs, asset IDs, component IDs) so the model can reference them in subsequent
    /// turns without re-querying.
    /// </summary>
    private static string SummarizeToolResultForContext(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return string.Empty;

        if (result.StartsWith("[MCP Error]", StringComparison.Ordinal))
            return Truncate(result, 120);

        // The tool result typically has two parts separated by newline:
        //   Line 1: human-readable message (e.g. "Found 1 nodes matching 'Floor'.")
        //   Line 2+: JSON data (e.g. {"nodes":[{"id":"<guid>","name":"Floor","path":"..."}]})
        // We want to keep the message AND extract compact ID mappings from the JSON data.

        string message = string.Empty;
        string? jsonPart = null;

        int firstNewline = result.IndexOf('\n');
        if (firstNewline > 0)
        {
            message = result[..firstNewline].Trim();
            string remainder = result[(firstNewline + 1)..].Trim();
            if (remainder.Length > 0 && (remainder[0] == '{' || remainder[0] == '['))
                jsonPart = remainder;
        }
        else
        {
            // Single line — might be pure JSON or pure text
            string trimmed = result.Trim();
            if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
                jsonPart = trimmed;
            else
                message = trimmed;
        }

        // If no JSON data, return the message (or truncated raw text)
        if (jsonPart is null)
            return Truncate(message, 200);

        // Extract ID mappings from JSON data
        var idParts = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(jsonPart);
            ExtractIdMappings(doc.RootElement, idParts, depth: 0);
        }
        catch { }

        if (idParts.Count == 0)
            return Truncate(message, 200);

        // Build: "Found 1 nodes matching 'Floor'. IDs: Floor=<guid>"
        string idSection = string.Join(", ", idParts);
        if (string.IsNullOrEmpty(message))
            return Truncate($"IDs: {idSection}", 500);

        return Truncate($"{message} IDs: {idSection}", 500);
    }

    /// <summary>
    /// Recursively extracts name→id mappings from JSON elements for context summaries.
    /// Handles common MCP patterns: arrays of {id, name}, objects with {id}, nested data.
    /// </summary>
    private static void ExtractIdMappings(JsonElement element, List<string> parts, int depth)
    {
        if (depth > 3 || parts.Count >= 20) // Safety limits
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                // Look for id/name pairs in this object
                string? id = null;
                string? name = null;
                foreach (string idKey in new[] { "id", "node_id", "asset_id", "component_id" })
                {
                    if (element.TryGetProperty(idKey, out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    {
                        id = idEl.GetString();
                        break;
                    }
                }
                if (element.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    name = nameEl.GetString();

                if (id is not null)
                {
                    parts.Add(name is not null ? $"{name}={id}" : id);
                    return; // Don't recurse further into this object — we captured it
                }

                // No direct id — recurse into child properties that are arrays/objects
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                        ExtractIdMappings(prop.Value, parts, depth + 1);
                }
                break;
            }
            case JsonValueKind.Array:
            {
                foreach (var item in element.EnumerateArray())
                    ExtractIdMappings(item, parts, depth + 1);
                break;
            }
        }
    }

    private static string StripInlineJsonSuffix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string normalized = text.Trim();

        int objectStart = normalized.IndexOf('{');
        int arrayStart = normalized.IndexOf('[');
        int jsonStart = objectStart switch
        {
            < 0 => arrayStart,
            _ when arrayStart < 0 => objectStart,
            _ => Math.Min(objectStart, arrayStart),
        };

        // If the text is pure JSON, keep original behavior for JSON parsing paths.
        if (jsonStart <= 0)
            return normalized;

        ReadOnlySpan<char> jsonTail = normalized.AsSpan(jsonStart).Trim();
        if (jsonTail.Length == 0)
            return normalized;

        try
        {
            using var _ = JsonDocument.Parse(jsonTail.ToString());
            return normalized[..jsonStart].TrimEnd();
        }
        catch
        {
            return normalized;
        }
    }

    private static string Truncate(string s, int maxLen)
    {
        if (s.Length <= maxLen)
            return s;

        if (maxLen <= 3)
            return s[..maxLen];

        return s[..(maxLen - 3)] + "...";
    }

    /// <summary>
    /// Executes a tool call against the local MCP server's <c>tools/call</c> endpoint.
    /// Returns the result text, or an error message on failure.
    /// </summary>
    private async Task<ToolCallResult> ExecuteMcpToolCallAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        if (string.Equals(toolName, "file_search", StringComparison.OrdinalIgnoreCase))
            return new ToolCallResult(await ExecuteLocalFileSearchToolAsync(argumentsJson, ct));

        if (string.Equals(toolName, "apply_patch", StringComparison.OrdinalIgnoreCase))
            return new ToolCallResult(await ExecuteLocalApplyPatchToolAsync(argumentsJson, ct));

        ToolCallResult FinalizeToolResult(string resultText, string? imagePath = null)
        {
            TryAutoFocusCameraForToolCall(toolName, argumentsJson, resultText);

            // If the result references an image file, encode it for multimodal feedback.
            string? resolvedImagePath = imagePath;
            if (resolvedImagePath is null)
                resolvedImagePath = TryExtractImagePath(resultText);

            string? imageBase64 = null;
            if (resolvedImagePath is not null && File.Exists(resolvedImagePath))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(resolvedImagePath);
                    imageBase64 = Convert.ToBase64String(bytes);
                }
                catch { /* Non-critical — model just won't get the image */ }
            }

            return new ToolCallResult(resultText, imageBase64, resolvedImagePath);
        }

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
                return new ToolCallResult($"[MCP Error] HTTP {(int)response.StatusCode}: {body}");

            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("error", out JsonElement errEl) && errEl.ValueKind == JsonValueKind.Object)
            {
                string? errMsg = errEl.TryGetProperty("message", out var m) ? m.GetString() : null;
                return new ToolCallResult($"[MCP Error] {errMsg ?? "Unknown error"}");
            }

            if (root.TryGetProperty("result", out JsonElement resultEl))
            {
                // Try to extract text from content array: { "content": [ { "type": "text", "text": "..." } ] }
                var sb = new StringBuilder();
                string? dataImagePath = null;
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

                    // Check for image path in data (e.g. screenshot results include "path")
                    if (dataEl.ValueKind == JsonValueKind.Object
                        && dataEl.TryGetProperty("path", out var pathEl)
                        && pathEl.ValueKind == JsonValueKind.String)
                    {
                        string? p = pathEl.GetString();
                        if (p is not null && IsImageFilePath(p))
                            dataImagePath = p;
                    }
                }

                if (sb.Length > 0)
                    return FinalizeToolResult(sb.ToString(), dataImagePath);

                // Fallback: serialize the entire result
                return FinalizeToolResult(resultEl.GetRawText(), dataImagePath);
            }

            return FinalizeToolResult(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ToolCallResult($"[MCP Error] Tool call '{toolName}' timed out after 30 seconds.");
        }
        catch (Exception ex)
        {
            return new ToolCallResult($"[MCP Error] {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if a file path points to a common image format.
    /// </summary>
    private static bool IsImageFilePath(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tries to find an image file path in a tool result text.
    /// Looks for common patterns like absolute paths ending in image extensions.
    /// </summary>
    private static string? TryExtractImagePath(string text)
    {
        // Look for Windows absolute paths (e.g. C:\...\file.png or D:\...\file.jpg)
        int searchStart = 0;
        while (searchStart < text.Length)
        {
            // Find a drive letter pattern
            int driveIdx = text.IndexOf(":\\", searchStart, StringComparison.Ordinal);
            if (driveIdx < 1) break;

            int pathStart = driveIdx - 1;
            if (!char.IsLetter(text[pathStart]))
            {
                searchStart = driveIdx + 2;
                continue;
            }

            // Walk forward to find the end of the path
            int pathEnd = pathStart;
            for (int i = driveIdx + 2; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\'' || c == '\n' || c == '\r' || c == ',' || c == '}' || c == ']' || c == ' ')
                    break;
                pathEnd = i;
            }

            string candidate = text[pathStart..(pathEnd + 1)];
            if (IsImageFilePath(candidate))
                return candidate;

            searchStart = pathEnd + 1;
        }

        return null;
    }

    private void TryAutoFocusCameraForToolCall(string toolName, string argumentsJson, string toolResult)
    {
        if (!AutoCameraView)
            return;

        if (!IsLikelySceneMutationTool(toolName))
            return;

        if (toolResult.StartsWith("[MCP Error]", StringComparison.Ordinal))
            return;

        HashSet<Guid> candidateNodeIds = CollectCandidateSceneNodeIds(argumentsJson, toolResult);
        if (candidateNodeIds.Count == 0)
            return;

        static SceneNode? ResolveCandidateNode(HashSet<Guid> ids)
        {
            XRWorldInstance? activeWorld = McpWorldResolver.TryGetActiveWorldInstance();
            foreach (Guid id in ids)
            {
                if (!XRObjectBase.ObjectsCache.TryGetValue(id, out var obj) || obj is not SceneNode node)
                    continue;

                if (activeWorld is not null && node.World != activeWorld)
                    continue;

                return node;
            }

            return null;
        }

        void FocusCamera()
        {
            SceneNode? targetNode = ResolveCandidateNode(candidateNodeIds);
            if (targetNode is null)
                return;

            if (Engine.State.MainPlayer.ControlledPawn is not EditorFlyingCameraPawnComponent pawn)
                return;

            pawn.FocusOnNode(targetNode, AutoCameraViewFocusDurationSeconds);
        }

        Engine.InvokeOnMainThread(FocusCamera, "MCP Assistant: Auto camera view", executeNowIfAlreadyMainThread: true);
    }

    private static bool IsLikelySceneMutationTool(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        string name = toolName.Trim();

        if (name.Contains("focus_node", StringComparison.OrdinalIgnoreCase))
            return false;

        if (name.StartsWith("list_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("get_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("read_", StringComparison.OrdinalIgnoreCase)
            || name.Contains("query", StringComparison.OrdinalIgnoreCase)
            || name.Contains("search", StringComparison.OrdinalIgnoreCase)
            || name.Contains("inspect", StringComparison.OrdinalIgnoreCase)
            || name.Contains("validate", StringComparison.OrdinalIgnoreCase)
            || name.Contains("debug", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] mutatingHints =
        [
            "create", "add", "spawn", "insert", "update", "set", "change", "modify",
            "move", "rotate", "scale", "reparent", "rename", "delete", "remove", "duplicate",
            "material", "component", "prefab", "attach", "detach"
        ];

        return mutatingHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<Guid> CollectCandidateSceneNodeIds(string argumentsJson, string toolResult)
    {
        var ids = new HashSet<Guid>();

        CollectGuidsFromText(argumentsJson, ids);
        CollectGuidsFromText(toolResult, ids);

        return ids;
    }

    private static void CollectGuidsFromText(string text, HashSet<Guid> ids)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (Match match in GuidPattern.Matches(text))
        {
            if (Guid.TryParse(match.Value, out Guid parsed))
                ids.Add(parsed);
        }
    }

    private static async Task<string> ExecuteLocalFileSearchToolAsync(string argumentsJson, CancellationToken ct)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            JsonElement root = doc.RootElement;

            string query = root.TryGetProperty("query", out var queryEl) && queryEl.ValueKind == JsonValueKind.String
                ? queryEl.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(query))
                return JsonSerializer.Serialize(new { ok = false, error = "Missing required 'query' argument." });

            string pattern = root.TryGetProperty("pattern", out var patternEl) && patternEl.ValueKind == JsonValueKind.String
                ? patternEl.GetString() ?? string.Empty
                : string.Empty;

            string searchRoot = root.TryGetProperty("root", out var rootEl) && rootEl.ValueKind == JsonValueKind.String
                ? rootEl.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(searchRoot))
                searchRoot = Environment.CurrentDirectory;

            int maxResults = root.TryGetProperty("max_results", out var maxEl) && maxEl.TryGetInt32(out int parsed)
                ? Math.Clamp(parsed, 1, 200)
                : 25;

            if (!Directory.Exists(searchRoot))
                return JsonSerializer.Serialize(new { ok = false, error = $"Search root does not exist: {searchRoot}" });

            var results = new List<object>();
            var ignoredSegments = new[] { "\\.git\\", "\\bin\\", "\\obj\\", "\\node_modules\\", "\\Build\\Submodules\\" };

            foreach (string filePath in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                string normalized = filePath.Replace('/', '\\');
                if (ignoredSegments.Any(seg => normalized.Contains(seg, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (!MatchesSimpleGlob(Path.GetFileName(filePath), pattern))
                    continue;

                int lineNumber = 0;
                string? firstHit = null;
                int firstHitLine = 0;

                foreach (string line in File.ReadLines(filePath))
                {
                    ct.ThrowIfCancellationRequested();
                    lineNumber++;

                    if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        firstHit = line.Trim();
                        firstHitLine = lineNumber;
                        break;
                    }
                }

                if (firstHit is null)
                    continue;

                string relativePath = Path.GetRelativePath(searchRoot, filePath).Replace('\\', '/');
                results.Add(new
                {
                    path = relativePath,
                    line = firstHitLine,
                    preview = Truncate(firstHit, 180)
                });

                if (results.Count >= maxResults)
                    break;
            }

            return JsonSerializer.Serialize(new
            {
                ok = true,
                root = searchRoot,
                query,
                pattern,
                count = results.Count,
                results
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
    }

    private static async Task<string> ExecuteLocalApplyPatchToolAsync(string argumentsJson, CancellationToken ct)
    {
        string? tempPatchPath = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            JsonElement root = doc.RootElement;

            string patch = root.TryGetProperty("patch", out var patchEl) && patchEl.ValueKind == JsonValueKind.String
                ? patchEl.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(patch))
                return JsonSerializer.Serialize(new { ok = false, error = "Missing required 'patch' argument." });

            string workRoot = root.TryGetProperty("root", out var rootEl) && rootEl.ValueKind == JsonValueKind.String
                ? rootEl.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(workRoot))
                workRoot = Environment.CurrentDirectory;

            bool dryRun = root.TryGetProperty("dry_run", out var dryEl)
                && dryEl.ValueKind == JsonValueKind.True;

            if (!Directory.Exists(workRoot))
                return JsonSerializer.Serialize(new { ok = false, error = $"Working directory does not exist: {workRoot}" });

            tempPatchPath = Path.Combine(Path.GetTempPath(), $"xrengine_patch_{Guid.NewGuid():N}.diff");
            await File.WriteAllTextAsync(tempPatchPath, patch, ct);

            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = workRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("apply");
            if (dryRun)
                psi.ArgumentList.Add("--check");
            psi.ArgumentList.Add("--whitespace=nowarn");
            psi.ArgumentList.Add(tempPatchPath);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Failed to start git process." });

            string stdout = await process.StandardOutput.ReadToEndAsync(ct);
            string stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            bool ok = process.ExitCode == 0;
            return JsonSerializer.Serialize(new
            {
                ok,
                dry_run = dryRun,
                exit_code = process.ExitCode,
                stdout,
                stderr
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPatchPath) && File.Exists(tempPatchPath))
            {
                try { File.Delete(tempPatchPath); } catch { }
            }
        }
    }

    private static bool MatchesSimpleGlob(string fileName, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return true;

        string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(fileName, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool TryExtractGeneratedImagePath(JsonElement root, out string? imagePath)
    {
        imagePath = null;
        try
        {
            if (!root.TryGetProperty("response", out JsonElement responseEl))
                return false;

            if (!responseEl.TryGetProperty("output", out JsonElement outputEl) || outputEl.ValueKind != JsonValueKind.Array)
                return false;

            foreach (JsonElement item in outputEl.EnumerateArray())
            {
                if (!TryFindFirstBase64Image(item, out string? b64) || string.IsNullOrWhiteSpace(b64))
                    continue;

                string folder = Path.Combine(Environment.CurrentDirectory, "McpCaptures", "GeneratedImages");
                Directory.CreateDirectory(folder);
                string filePath = Path.Combine(folder, $"Generated_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
                File.WriteAllBytes(filePath, Convert.FromBase64String(b64));
                imagePath = filePath;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryFindFirstBase64Image(JsonElement element, out string? base64)
    {
        base64 = null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in element.EnumerateObject())
            {
                if (prop.NameEquals("b64_json") && prop.Value.ValueKind == JsonValueKind.String)
                {
                    base64 = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(base64))
                        return true;
                }

                if (TryFindFirstBase64Image(prop.Value, out base64))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                if (TryFindFirstBase64Image(child, out base64))
                    return true;
            }
        }

        return false;
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

    private sealed class RealtimeFunctionResult
    {
        public bool IsError { get; init; }
        public string FunctionOutput { get; init; } = string.Empty;
        public string ImageDataUrl { get; init; } = string.Empty;
        public string ContextText { get; init; } = string.Empty;
    }

    private sealed class RealtimeScreenshotCapture
    {
        public string Path { get; init; } = string.Empty;
        public string? CameraNodeId { get; init; }
        public string? CameraName { get; init; }
        public int WindowIndex { get; init; }
        public int ViewportIndex { get; init; }
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
