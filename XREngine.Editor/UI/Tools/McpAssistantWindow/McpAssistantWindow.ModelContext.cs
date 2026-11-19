using System.Net.Http.Headers;
using System.Text.Json;

namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private void RefreshSelectedModelContextWindow()
    {
        if (Prefs?.McpAssistantUseCodexAppServer == true || ProviderIndex != (int)ProviderType.Codex)
            return;

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

