using ImGuiNET;

namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private int _catalogProvider = -1;
    private bool _catalogSubscription;
    private string _catalogCredential = string.Empty;
    private CancellationTokenSource? _catalogCancellation;
    private Task<(IReadOnlyList<AssistantModelOption> Models, string Status)>? _catalogTask;
    private IReadOnlyList<AssistantModelOption> _catalogModels = [];
    private bool _catalogRequested;
    private string _catalogStatus = "Connect to load models.";
    private string _modelPreview = "Choose a model";
    private string? _previewModel;
    private bool _previewRealtime;
    private bool _apiModelReady;

    private bool UsesCodexSubscription => ProviderIndex == (int)ProviderType.Codex && Prefs?.McpAssistantUseCodexAppServer == true;
    private bool UsesSubscription => UsesCodexSubscription
        || (ProviderIndex == (int)ProviderType.ClaudeCode && Prefs?.McpAssistantUseClaudeCli == true)
        || ProviderIndex == (int)ProviderType.Copilot
        || (ProviderIndex == (int)ProviderType.Gemini && Prefs?.McpAssistantUseGeminiCli == true);
    private SubscriptionAssistantBrand SubscriptionBrand => (SubscriptionAssistantBrand)ProviderIndex;
    private bool IsApiBrand => ProviderIndex is (int)ProviderType.Codex or (int)ProviderType.ClaudeCode or (int)ProviderType.Gemini;

    private string GetBrandLabel() => (ProviderType)ProviderIndex switch
    {
        ProviderType.Codex => "Codex",
        ProviderType.ClaudeCode => "Claude",
        ProviderType.Copilot => "Copilot",
        ProviderType.Gemini => "Gemini",
        ProviderType.GitHubModels => "GitHub Models (retired)",
        _ => "Choose an assistant",
    };

    private string GetSelectedModel() => (ProviderType)ProviderIndex switch
    {
        _ when UsesSubscription => SubscriptionEditorConnection.Model(SubscriptionBrand),
        ProviderType.Codex when UseRealtimeWebSocket => OpenAiRealtimeModel,
        ProviderType.Codex => OpenAiModel,
        ProviderType.ClaudeCode => AnthropicModel,
        ProviderType.Gemini => GeminiModel,
        _ => string.Empty,
    };

    private string GetApiCredential() => (ProviderType)ProviderIndex switch
    {
        _ when UsesSubscription => string.Empty,
        ProviderType.Codex when !UsesCodexSubscription => OpenAiApiKey,
        ProviderType.ClaudeCode => AnthropicApiKey,
        ProviderType.Gemini => GeminiApiKey,
        _ => string.Empty,
    };

    private string GetApiKeyEnvironmentName() => (ProviderType)ProviderIndex switch
    {
        ProviderType.Codex => "OPENAI_API_KEY",
        ProviderType.ClaudeCode => "ANTHROPIC_API_KEY",
        ProviderType.Gemini => "GEMINI_API_KEY",
        _ => string.Empty,
    };

    private string GetApiKeyHelp() => (ProviderType)ProviderIndex switch
    {
        ProviderType.Codex => "Copy an API key from your OpenAI developer account, or use OPENAI_API_KEY from your environment.",
        ProviderType.ClaudeCode => "Copy an API key from Claude Console, or use ANTHROPIC_API_KEY from your environment.",
        ProviderType.Gemini => "Copy an API key from Google AI Studio, or use GEMINI_API_KEY from your environment.",
        _ => string.Empty,
    };

    private void SetApiCredential(string value, string environmentName = "")
    {
        if (Prefs is not { } preferences)
            return;
        switch ((ProviderType)ProviderIndex)
        {
            case ProviderType.Codex:
                preferences.McpAssistantOpenAiApiKeyEnvVar = environmentName;
                OpenAiApiKey = value;
                break;
            case ProviderType.ClaudeCode:
                preferences.McpAssistantAnthropicApiKeyEnvVar = environmentName;
                AnthropicApiKey = value;
                break;
            case ProviderType.Gemini:
                preferences.McpAssistantGeminiApiKeyEnvVar = environmentName;
                GeminiApiKey = value;
                break;
        }
        StartNewConversation();
        InvalidateApiCatalog();
    }

    private bool CanSendWithSelectedModel()
        => IsEffortSelectionValid() && (UsesSubscription ? SubscriptionEditorConnection.IsModelReady(SubscriptionBrand)
            : IsApiBrand && _apiModelReady && _catalogTask is null
                && _catalogProvider == ProviderIndex && _catalogCredential == GetApiCredential()
                && _previewModel == GetSelectedModel());

    private string GetConnectionStatus()
    {
        if (!IsEffortSelectionValid())
            return "Choose an available effort level or Default.";
        if (UsesSubscription)
        {
            if (SubscriptionEditorConnection.IsChecking(SubscriptionBrand))
                return "Checking connection...";
            if (SubscriptionEditorConnection.IsSigningIn(SubscriptionBrand))
                return "Waiting for sign-in...";
            if (SubscriptionEditorConnection.IsModelReady(SubscriptionBrand))
                return SubscriptionEditorConnection.SupportsConversationReplay(SubscriptionBrand)
                    ? "Connected · Using your subscription"
                    : "Connected · Subscription · Each request starts a new chat";
            return SubscriptionEditorConnection.IsConnected(SubscriptionBrand)
                ? "Select an available model."
                : SubscriptionEditorConnection.Status(SubscriptionBrand);
        }
        return (ProviderType)ProviderIndex switch
        {
            ProviderType.GitHubModels => "This saved connection is retired. Choose another assistant.",
            _ when !IsApiBrand => "Choose an assistant.",
            _ when string.IsNullOrWhiteSpace(GetApiCredential()) => "Connect with an API key to load models.",
            _ when _catalogTask is not null => "Loading models...",
            _ when _apiModelReady => "Connected · API usage billed separately",
            _ when _catalogModels.Count > 0 => "Select an available model.",
            _ => _catalogStatus,
        };
    }

    /// <summary>
    /// Publishes only the current connection's catalog on the UI thread. A key or
    /// provider change cancels discovery and discards its pending response.
    /// </summary>
    private void UpdateModelCatalog()
    {
        string credential = GetApiCredential();
        bool subscription = UsesSubscription;
        if (_catalogProvider != ProviderIndex || _catalogSubscription != subscription || _catalogCredential != credential)
        {
            InvalidateApiCatalog();
            _catalogProvider = ProviderIndex;
            _catalogSubscription = subscription;
            _catalogCredential = credential;
        }
        if (subscription)
        {
            SubscriptionEditorConnection.EnsureConnectionChecked(SubscriptionBrand);
            return;
        }
        if (!IsApiBrand || string.IsNullOrWhiteSpace(credential))
            return;
        if (_catalogTask is { IsCompleted: true })
        {
            (_catalogModels, _catalogStatus) = _catalogTask.GetAwaiter().GetResult();
            _catalogTask = null;
            _catalogCancellation?.Dispose();
            _catalogCancellation = null;
            if (ProviderIndex == (int)ProviderType.Codex)
            {
                foreach (AssistantModelOption model in _catalogModels)
                    if (model.ContextWindowTokens is > 0)
                        _openAiContextWindowByModel[model.Id] = model.ContextWindowTokens.Value;
            }
            RefreshApiModelSelection();
        }
        if (!_catalogRequested)
        {
            _catalogRequested = true;
            _catalogCancellation = new CancellationTokenSource();
            AssistantCatalogProvider provider = (ProviderType)ProviderIndex switch
            {
                ProviderType.ClaudeCode => AssistantCatalogProvider.Claude,
                ProviderType.Gemini => AssistantCatalogProvider.Gemini,
                _ => AssistantCatalogProvider.OpenAi,
            };
            _catalogTask = LoadCatalogSafelyAsync(provider, credential, _catalogCancellation.Token);
        }
        if (_previewModel != GetSelectedModel() || _previewRealtime != UseRealtimeWebSocket)
            RefreshApiModelSelection();
    }

    private static async Task<(IReadOnlyList<AssistantModelOption>, string)> LoadCatalogSafelyAsync(
        AssistantCatalogProvider provider, string credential, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<AssistantModelOption> models = await AssistantModelCatalog.LoadAsync(provider, credential, cancellationToken);
            return (models, models.Count == 0 ? "No compatible models are available for this account." : "Models loaded.");
        }
        catch (OperationCanceledException)
        {
            return ([], "Model loading stopped. Use Refresh models to try again.");
        }
        catch (Exception)
        {
            // Never put provider error bodies, credentials, or request URLs in UI diagnostics.
            return ([], "Could not load models. Check your API key and connection, then refresh.");
        }
    }

    private void InvalidateApiCatalog()
    {
        _catalogCancellation?.Cancel();
        _catalogCancellation?.Dispose();
        _catalogCancellation = null;
        _catalogTask = null;
        _catalogRequested = false;
        _catalogModels = [];
        _apiModelReady = false;
        _previewModel = null;
        _modelPreview = "Choose a model";
        _catalogStatus = "Connect to load models.";
    }

    private void RefreshApiModelSelection()
    {
        _previewModel = GetSelectedModel();
        _previewRealtime = UseRealtimeWebSocket;
        _apiModelReady = false;
        _modelPreview = "Choose a model";
        foreach (AssistantModelOption model in _catalogModels)
        {
            if (IsModelVisible(model) && model.Id == _previewModel)
            {
                _modelPreview = model.DisplayName;
                _apiModelReady = true;
                break;
            }
        }
    }

    private bool IsModelVisible(AssistantModelOption model)
        => model.IsRealtime == (ProviderIndex == (int)ProviderType.Codex && UseRealtimeWebSocket);

    private void DrawApiModelPicker()
    {
        bool disabled = !IsApiBrand || _catalogModels.Count == 0 || _catalogTask is not null;
        ImGui.BeginDisabled(disabled);
        if (ImGui.BeginCombo("##AssistantModel", _modelPreview))
        {
            for (int i = 0; i < _catalogModels.Count; i++)
            {
                AssistantModelOption model = _catalogModels[i];
                if (!IsModelVisible(model))
                    continue;
                ImGui.PushID(model.Id);
                if (ImGui.Selectable(model.DisplayName, model.Id == GetSelectedModel()))
                {
                    switch ((ProviderType)ProviderIndex)
                    {
                        case ProviderType.Codex when UseRealtimeWebSocket: OpenAiRealtimeModel = model.Id; break;
                        case ProviderType.Codex: OpenAiModel = model.Id; RefreshSelectedModelContextWindow(); break;
                        case ProviderType.ClaudeCode: AnthropicModel = model.Id; break;
                        case ProviderType.Gemini: GeminiModel = model.Id; break;
                    }
                    RefreshApiModelSelection();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(model.Id);
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
    }
}
