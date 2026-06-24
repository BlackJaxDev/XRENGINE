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

public sealed partial class McpAssistantWindow
{
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
                ClearHistoryPreservingSentAssistantMessages();
                _conversationSummary = string.Empty;
                _historyCompactedThroughExclusive = 0;
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
        if (ImGui.InputInt("Max Auto Re-prompts", ref maxAutoReprompts, 1, 5))
            MaxAutoReprompts = Math.Clamp(maxAutoReprompts, 0, 50);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Maximum automatic continue prompts sent for one user prompt when the assistant emits the continue marker or does not emit the done marker.");

        bool autoSummarizeNearLimit = AutoSummarizeNearContextLimit;
        if (ImGui.Checkbox("Auto summarize near context limit", ref autoSummarizeNearLimit))
            AutoSummarizeNearContextLimit = autoSummarizeNearLimit;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, the assistant automatically compacts older turns into a retained summary before new sends and still asks the model for an extra summary when long-running work approaches the selected model's context window.");

        bool autoCameraView = AutoCameraView;
        if (ImGui.Checkbox("Auto Camera View", ref autoCameraView))
            AutoCameraView = autoCameraView;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, the editor camera smoothly focuses relevant scene nodes after assistant scene-edit tool calls.");

        bool verboseAiLogging = VerboseAiLogging;
        if (ImGui.Checkbox("Verbose AI Logging", ref verboseAiLogging))
            VerboseAiLogging = verboseAiLogging;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, logs full prompt envelopes, full model responses, and full MCP tool call payloads/results to the AI log category.");

        if (ImGui.Button("Load Keys from ENV"))
        {
            OpenAiApiKey = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenAiApiKey) ?? OpenAiApiKey;
            AnthropicApiKey = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.AnthropicApiKey) ?? AnthropicApiKey;
            GeminiApiKey = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.GeminiApiKey) ?? GeminiApiKey;
            GitHubModelsToken = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.GitHubToken) ?? GitHubModelsToken;
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
}
