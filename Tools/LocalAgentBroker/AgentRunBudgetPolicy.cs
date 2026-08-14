using System.Text.Json;
using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Resolves broker-owned defaults without changing an explicitly authorized
/// run budget.
/// </summary>
internal static class AgentRunBudgetPolicy
{
    internal const int DefaultMaxOutputTokens = 4_096;
    internal const int DefaultSolMaxOutputTokens = 16_384;
    internal const int DefaultSolIntensiveMaxOutputTokens = 32_768;
    internal const int DefaultMaxElapsedSeconds = 120;
    internal const int DefaultSolMaxElapsedSeconds = 300;
    internal const int DefaultSolIntensiveMaxElapsedSeconds = 600;

    internal static AgentRunRequest ApplyDefaults(
        AgentRunRequest request,
        in JsonElement arguments)
    {
        if (request.Budget is null)
            return request;

        bool hasExplicitMaxOutputTokens = HasExplicitBudgetProperty(
            arguments,
            "max_output_tokens",
            "maxOutputTokens");
        bool hasExplicitMaxElapsedSeconds = HasExplicitBudgetProperty(
            arguments,
            "max_elapsed_seconds",
            "maxElapsedSeconds");
        int maxOutputTokens = hasExplicitMaxOutputTokens
            ? request.Budget.MaxOutputTokens
            : ResolveDefaultMaxOutputTokens(request.RequestedModel, request.ReasoningEffort);
        int maxElapsedSeconds = hasExplicitMaxElapsedSeconds
            ? request.Budget.MaxElapsedSeconds
            : ResolveDefaultMaxElapsedSeconds(request.RequestedModel, request.ReasoningEffort);
        if (maxOutputTokens == request.Budget.MaxOutputTokens &&
            maxElapsedSeconds == request.Budget.MaxElapsedSeconds)
        {
            return request;
        }

        return request with
        {
            Budget = request.Budget with
            {
                MaxOutputTokens = maxOutputTokens,
                MaxElapsedSeconds = maxElapsedSeconds,
            },
        };
    }

    internal static int ResolveDefaultMaxOutputTokens(
        string requestedModel,
        string reasoningEffort)
    {
        if (!string.Equals(requestedModel, AgentModelCatalog.Sol, StringComparison.Ordinal))
            return DefaultMaxOutputTokens;

        return string.Equals(reasoningEffort, "xhigh", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reasoningEffort, "max", StringComparison.OrdinalIgnoreCase)
            ? DefaultSolIntensiveMaxOutputTokens
            : DefaultSolMaxOutputTokens;
    }

    internal static int ResolveDefaultMaxElapsedSeconds(
        string requestedModel,
        string reasoningEffort)
    {
        if (!string.Equals(requestedModel, AgentModelCatalog.Sol, StringComparison.Ordinal))
            return DefaultMaxElapsedSeconds;

        return string.Equals(reasoningEffort, "xhigh", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reasoningEffort, "max", StringComparison.OrdinalIgnoreCase)
            ? DefaultSolIntensiveMaxElapsedSeconds
            : DefaultSolMaxElapsedSeconds;
    }

    private static bool HasExplicitBudgetProperty(
        in JsonElement arguments,
        string snakeCaseName,
        string camelCaseName)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("budget", out JsonElement budget) ||
            budget.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in budget.EnumerateObject())
        {
            if (property.Name.Equals(snakeCaseName, StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals(camelCaseName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
