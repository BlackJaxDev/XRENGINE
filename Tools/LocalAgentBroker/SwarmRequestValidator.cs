using System.Text.Json;
using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>Admission boundaries for opt-in hierarchical code generation.</summary>
internal static class SwarmRequestValidator
{
    public static void Validate(AgentRunRequest request)
    {
        if (request.Swarm is not { } swarm)
            return;
        if (request.Objective.Length > 8_000)
            throw new ArgumentException("Swarm objectives cannot exceed 8000 characters; use context_files for supporting source.");
        if (swarm.AutoApply && !OperatingSystem.IsWindows())
            throw new ArgumentException("Swarm auto_apply currently requires Windows handle validation; reviewed proposals are supported on other platforms.");
        // Reserve room for every ancestor's exact artifact review under the shared request cap.
        if (JsonSerializer.Serialize(request).Length > 65_536)
            throw new ArgumentException("Swarm request metadata cannot exceed 65536 serialized characters; use context_files for source content.");
        if (request.RequestedModel != AgentModelCatalog.Luna6 || request.ReasoningEffort != "max")
            throw new ArgumentException("Swarms require requested_model 'gpt-6-luna' and reasoning_effort 'max'.");
        if (request.EditorSession is not null || request.RepositoryAccess.Enabled || request.RequireToolUse
            || request.HostedTools.Count > 0 || request.ToolPolicy.AllowMutation || request.ToolPolicy.AllowDestructive
            || request.UseBackgroundMode || !request.UseCompactHandoffPrompt || request.InitialImageDataUri is not null)
            throw new ArgumentException("Swarms use immutable source snapshots only; editor/repository tools, hosted tools, images, background mode, and tool mutation are unavailable. Use swarm.auto_apply for reviewed host writes.");
        if (swarm.AllowedPaths is null || swarm.AllowedPaths.Count is < 1 or > 64
            || swarm.AllowedPaths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("swarm.allowed_paths must contain 1 to 64 exact source file paths.");
        Check(swarm.MaxDepth, 1, 6, "max_depth");
        Check(swarm.MaxAgents, 2, 64, "max_agents");
        Check(swarm.MaxChildren, 1, 8, "max_children");
        Check(swarm.MaxParallelAgents, 1, 8, "max_parallel_agents");
        Check(swarm.MaxOutputTokens, 16, 1_048_576, "max_output_tokens");
        Check(swarm.MaxPhaseOutputTokens, 16, 32_768, "max_phase_output_tokens");
        Check(swarm.MaxElapsedSeconds, 1, 3_600, "max_elapsed_seconds");
        Check(swarm.MaxChangedLinesPerLeaf, 1, 500, "max_changed_lines_per_leaf");
        if (swarm.MaxPhaseOutputTokens > swarm.MaxOutputTokens)
            throw new ArgumentException("swarm.max_phase_output_tokens cannot exceed max_output_tokens.");
    }

    private static void Check(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentException($"swarm.{name} must be between {minimum} and {maximum}.");
    }
}
