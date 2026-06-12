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
        bool mutationLikelyRequired = AttachMcpServer && IsLikelySceneMutationPrompt(trimmedPrompt);

        int maxAutoReprompts = Math.Clamp(MaxAutoReprompts, 0, 50);
        bool finishedByMarker = false;
        bool reachedRepromptLimit = false;

        SetStatus("Streaming\u2026", ColorBusy);
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            for (int attempt = 0; attempt <= maxAutoReprompts; attempt++)
            {
                await AutoCompactConversationContextIfNeededAsync(provider, trimmedPrompt, assistantMsg, minTailMessagesToKeep: 2, _cts.Token);
                bool requestSelfSummary = ShouldRequestSelfSummary(provider, trimmedPrompt, assistantMsg);
                string promptForAttempt = BuildProviderPromptEnvelope(trimmedPrompt, assistantMsg, attempt, requestSelfSummary);
                string priorContent = assistantMsg.Content;
                LogAiPromptAttempt(provider, attempt, maxAutoReprompts, promptForAttempt, isContinueFlow: false, requestSelfSummary);

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

                LogAiResponseAttempt(provider, attempt, maxAutoReprompts, assistantMsg.Content, isContinueFlow: false);

                CaptureContextSummary(assistantMsg);

                // Check for explicit continue marker — model is requesting auto-reprompt.
                if (TryStripContinueMarker(assistantMsg))
                {
                    if (attempt >= maxAutoReprompts)
                    {
                        reachedRepromptLimit = true;
                        break;
                    }

                    SetStatus($"Model requested continuation; auto re-prompt {attempt + 1}/{maxAutoReprompts}\u2026", ColorBusy);
                    continue;
                }

                if (TryStripDoneMarker(assistantMsg))
                {
                    bool missingRequiredMutation = mutationLikelyRequired && !HasSuccessfulMutationToolCall(assistantMsg);
                    if (IndicatesRemainingWork(assistantMsg.Content) || missingRequiredMutation)
                    {
                        if (attempt >= maxAutoReprompts)
                        {
                            reachedRepromptLimit = true;
                            break;
                        }

                        if (missingRequiredMutation)
                            SetStatus($"Model signaled done without any successful mutating tool calls; auto re-prompting {attempt + 1}/{maxAutoReprompts}…", ColorBusy);
                        else
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

        int maxAutoReprompts = Math.Clamp(MaxAutoReprompts, 0, 50);
        bool finishedByMarker = false;
        bool reachedRepromptLimit = false;
        bool mutationLikelyRequired = AttachMcpServer && IsLikelySceneMutationPrompt(trimmedPrompt);

        SetStatus("Continuing\u2026", ColorBusy);
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            for (int attempt = 0; attempt <= maxAutoReprompts; attempt++)
            {
                await AutoCompactConversationContextIfNeededAsync(provider, trimmedPrompt, assistantMsg, minTailMessagesToKeep: 2, _cts.Token);
                bool requestSelfSummary = ShouldRequestSelfSummary(provider, trimmedPrompt, assistantMsg);
                string promptForAttempt = BuildProviderPromptEnvelope(trimmedPrompt, assistantMsg, attempt, requestSelfSummary);
                string priorContent = assistantMsg.Content;
                LogAiPromptAttempt(provider, attempt, maxAutoReprompts, promptForAttempt, isContinueFlow: true, requestSelfSummary);

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

                LogAiResponseAttempt(provider, attempt, maxAutoReprompts, assistantMsg.Content, isContinueFlow: true);

                CaptureContextSummary(assistantMsg);

                // Check for explicit continue marker — model is requesting auto-reprompt.
                if (TryStripContinueMarker(assistantMsg))
                {
                    if (attempt >= maxAutoReprompts)
                    {
                        reachedRepromptLimit = true;
                        break;
                    }

                    SetStatus($"Model requested continuation; auto re-prompt {attempt + 1}/{maxAutoReprompts}\u2026", ColorBusy);
                    continue;
                }

                if (TryStripDoneMarker(assistantMsg))
                {
                    bool missingRequiredMutation = mutationLikelyRequired && !HasSuccessfulMutationToolCall(assistantMsg);
                    if (IndicatesRemainingWork(assistantMsg.Content) || missingRequiredMutation)
                    {
                        if (attempt >= maxAutoReprompts)
                        {
                            reachedRepromptLimit = true;
                            break;
                        }

                        if (missingRequiredMutation)
                            SetStatus($"Model signaled done without any successful mutating tool calls; auto re-prompting {attempt + 1}/{maxAutoReprompts}…", ColorBusy);
                        else
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
}
