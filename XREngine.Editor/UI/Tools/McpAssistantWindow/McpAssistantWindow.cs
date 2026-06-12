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
public sealed partial class McpAssistantWindow
{
    // ── Constants ────────────────────────────────────────────────────────

    private static readonly string[] ProviderLabels =
    [
        "Codex (OpenAI)",
        "Claude Code (Anthropic)",
        "Gemini (Google)",
        "GitHub Models"
    ];

    private const string AssistantDoneMarker = "[[XRENGINE_ASSISTANT_DONE]]";
    private const string AssistantContinueMarker = "[[XRENGINE_ASSISTANT_CONTINUE]]";
    private const string ContextSummaryStartMarker = "[[XRENGINE_CONTEXT_SUMMARY]]";
    private const string ContextSummaryEndMarker = "[[/XRENGINE_CONTEXT_SUMMARY]]";
    private const float ContextSummaryTriggerRatio = 0.80f;
    private const float ContextToolReferenceBudgetRatio = 0.35f;
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
    private readonly Dictionary<ChatMessage, float> _chatRowHeightCache = [];
    private int _lastChatHistoryCount;
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
    private int _historyCompactedThroughExclusive;
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
    private static int s_nextContentSegmentUiId;

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
        get => Prefs?.McpAssistantMaxAutoReprompts ?? 25;
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

    private bool VerboseAiLogging
    {
        get => Prefs?.McpAssistantVerboseAiLogging ?? true;
        set { if (Prefs is { } p) p.McpAssistantVerboseAiLogging = value; }
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

}
