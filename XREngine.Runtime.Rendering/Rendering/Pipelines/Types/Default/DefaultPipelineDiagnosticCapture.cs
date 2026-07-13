using System;
using System.IO;

namespace XREngine.Rendering;

internal static class DefaultPipelineDiagnosticCapture
{
    private static readonly string FallbackRunRoot = Path.Combine(
        "Build",
        "_AgentValidation",
        "manual-default-pipeline-capture");

    public static int ResolveLayerCount(bool stereo)
        => stereo ? 2 : 1;

    public static string ResolveOutputPath(string pipelineName, string label, int layerIndex)
    {
        string outputDirectory = ResolveOutputDirectory(
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.CaptureDefaultPipelineOutputDirectory),
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.AgentValidationRunRoot));
        return Path.Combine(outputDirectory, $"{pipelineName}_{label}_layer{layerIndex}.png");
    }

    public static string ResolveTemporalScenarioOutputPath(
        string pipelineName,
        EPhase524bTemporalSample sample,
        string label,
        int layerIndex)
    {
        string outputDirectory = ResolveOutputDirectory(
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.CaptureDefaultPipelineOutputDirectory),
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.AgentValidationRunRoot));
        return Path.Combine(
            outputDirectory,
            $"{pipelineName}_Temporal_{sample}_{label}_layer{layerIndex}.png");
    }

    internal static string ResolveOutputDirectory(string? configuredOutputDirectory, string? agentRunRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredOutputDirectory))
            return configuredOutputDirectory;

        string runRoot = string.IsNullOrWhiteSpace(agentRunRoot)
            ? FallbackRunRoot
            : agentRunRoot;
        return Path.Combine(runRoot, "mcp-captures");
    }
}
