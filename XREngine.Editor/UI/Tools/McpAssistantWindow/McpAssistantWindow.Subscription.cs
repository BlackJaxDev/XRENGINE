using System.Text;

namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private string? _subscriptionHistoryIdentity;
    private int _subscriptionHistoryStartIndex;
    private int _observedProviderIndex = -1;
    private bool? _observedSubscriptionAccessMode;

    private void ObserveAssistantConnectionSelection()
    {
        int providerIndex = ProviderIndex;
        bool subscriptionAccessMode = UsesSubscription;
        if (_observedProviderIndex == providerIndex && _observedSubscriptionAccessMode == subscriptionAccessMode)
            return;

        _observedProviderIndex = providerIndex;
        _observedSubscriptionAccessMode = subscriptionAccessMode;
        _canContinueAfterRepromptLimit = false;
        ResetSubscriptionHistoryBoundary();
    }

    private void ResetSubscriptionHistoryBoundary()
    {
        _subscriptionHistoryIdentity = null;
        _subscriptionHistoryStartIndex = _history.Count;
    }

    private async Task SendSubscriptionPromptAsync(string prompt)
    {
        _isBusy = true;
        _canContinueAfterRepromptLimit = false;
        string objective = BuildSubscriptionConversationPrompt(prompt);
        _history.Add(new ChatMessage { Role = "user", Content = prompt });
        _prompt = string.Empty;
        _pendingPromptClear = true;
        _scrollToBottom = true;

        var target = new ChatMessage
        {
            Role = "assistant", IsStreaming = true, Status = ChatMessageStatus.Running
        };
        SetMessageExplanationContext(target, prompt);
        _history.Add(target);
        var usage = new McpUsageEntry
        {
            Provider = $"{GetBrandLabel()} (signed-in)",
            PromptPreview = prompt.Length > 96 ? prompt[..96] + "..." : prompt,
            AttachRequested = AttachMcpServer,
            Result = "Pending"
        };
        AddMcpUsageEntry(usage);
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        try
        {
            if (AttachMcpServer)
            {
                SetStatus("Validating MCP connection...", ColorBusy);
                EnsureMcpServerAutoEnabledForAssistantMessage();
                using var preflight = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                (bool ready, string reason) = await EnsureMcpServerAttachReadyAsync(preflight.Token);
                if (!ready)
                    throw new InvalidOperationException($"MCP unavailable: {reason}");
            }

            _pendingScreenshotBase64 = null;
            if (AttachViewportScreenshot)
            {
                SetStatus("Capturing screenshot...", ColorBusy);
                _pendingScreenshotBase64 = await CaptureViewportScreenshotBase64Async(_cts.Token);
            }

            SetStatus("Running assistant...", ColorBusy);
            await StreamOpenAiResponsesAsync(
                objective,
                target,
                _cts.Token,
                usage,
                useSubscription: true,
                currentPromptLikelyNeedsTools: IsLikelySceneMutationPrompt(prompt));
            SetStatus(target.Status == ChatMessageStatus.Completed ? "Done." : target.StatusDetail ?? usage.Result,
                target.Status == ChatMessageStatus.Completed ? ColorDone : ColorError);
        }
        catch (OperationCanceledException)
        {
            usage.Result = "Canceled";
            target.Status = ChatMessageStatus.Stopped;
            target.StatusDetail = "The request stopped. Any completed changes may remain in the editor.";
            SetStatus("Canceled.", ColorError);
        }
        catch (Exception ex)
        {
            usage.Result = "Failed";
            usage.Note = ex.Message;
            target.Status = ChatMessageStatus.Failed;
            target.StatusDetail = ex.Message;
            SetStatus("Assistant failed.", ColorError);
        }
        finally
        {
            _pendingScreenshotBase64 = null;
            target.IsStreaming = false;
            _cts?.Dispose();
            _cts = null;
            _isBusy = false;
        }
    }

    private string BuildSubscriptionConversationPrompt(string prompt)
    {
        ObserveAssistantConnectionSelection();
        if (!SubscriptionEditorConnection.SupportsConversationReplay(SubscriptionBrand))
        {
            ResetSubscriptionHistoryBoundary();
            return prompt;
        }
        const int maxHistoryCharacters = 48_000;
        string identity = $"{SubscriptionEditorConnection.ConnectionRevision(SubscriptionBrand)}:{SubscriptionEditorConnection.Model(SubscriptionBrand)}:"
            + $"{SubscriptionBrand}:{AttachMcpServer}";
        if (!string.Equals(identity, _subscriptionHistoryIdentity, StringComparison.Ordinal))
        {
            ResetSubscriptionHistoryBoundary();
            _subscriptionHistoryIdentity = identity;
        }

        var history = new StringBuilder();
        var recent = new List<string>();
        int usedCharacters = 0;
        int start = Math.Max(_subscriptionHistoryStartIndex, _history.Count - 12);
        for (int i = _history.Count - 1; i >= start; i--)
        {
            int remaining = maxHistoryCharacters - usedCharacters - 2;
            if (remaining < 256)
                break;
            string? contextLine = BuildBoundedAssistantContextLine(_history[i], Math.Min(8000, remaining));
            if (string.IsNullOrEmpty(contextLine))
                continue;
            if (contextLine.Length > remaining)
                break;
            recent.Add(contextLine);
            usedCharacters += contextLine.Length + 2;
        }
        for (int i = recent.Count - 1; i >= 0; i--)
            history.AppendLine(recent[i]).AppendLine();

        if (history.Length == 0)
            return prompt;

        history.Append("user:\n").Append(prompt);
        return history.ToString();
    }
}
