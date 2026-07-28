using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Invokes a configurable autonomous LLM command in proposal and implementation phases.
/// </summary>
public sealed class SelfIterationAgentRunner
{
    private readonly string _workspaceRoot;
    private readonly string _runRoot;
    private readonly SelfIterationAgentConfiguration _configuration;
    private readonly SelfIterationProcessRunner _processRunner;
    private readonly JsonSerializerOptions _jsonOptions;

    public SelfIterationAgentRunner(
        string workspaceRoot,
        string runRoot,
        SelfIterationAgentConfiguration configuration,
        SelfIterationProcessRunner processRunner)
    {
        _workspaceRoot = workspaceRoot;
        _runRoot = runRoot;
        _configuration = configuration;
        _processRunner = processRunner;
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<SelfIterationAgentProposal> ProposeAsync(
        string prompt,
        string phaseDirectory,
        CancellationToken token)
    {
        string response = await RunPhaseAsync(
            prompt,
            _configuration.ProposalArguments,
            phaseDirectory,
            "proposal",
            token);
        SelfIterationAgentProposal proposal =
            JsonSerializer.Deserialize<SelfIterationAgentProposal>(ExtractJsonObject(response), _jsonOptions)
            ?? throw new InvalidDataException("Agent returned an empty proposal.");
        return proposal;
    }

    public async Task<SelfIterationAgentImplementation> ImplementAsync(
        string prompt,
        string phaseDirectory,
        CancellationToken token)
    {
        string response = await RunPhaseAsync(
            prompt,
            _configuration.ImplementationArguments,
            phaseDirectory,
            "implementation",
            token);
        return JsonSerializer.Deserialize<SelfIterationAgentImplementation>(
                ExtractJsonObject(response),
                _jsonOptions)
            ?? throw new InvalidDataException("Agent returned an empty implementation response.");
    }

    private async Task<string> RunPhaseAsync(
        string prompt,
        string[] argumentTemplates,
        string phaseDirectory,
        string phase,
        CancellationToken token)
    {
        Directory.CreateDirectory(phaseDirectory);
        string promptPath = Path.Combine(phaseDirectory, $"{phase}-prompt.md");
        string responsePath = Path.Combine(phaseDirectory, $"{phase}-response.json");
        await File.WriteAllTextAsync(promptPath, prompt, token);

        string[] arguments = argumentTemplates
            .Select(argument => ReplacePlaceholders(argument, promptPath, responsePath))
            .ToArray();
        SelfIterationProcessResult result = await _processRunner.RunAsync(
            _configuration.Executable,
            arguments,
            _workspaceRoot,
            TimeSpan.FromSeconds(_configuration.TimeoutSeconds),
            phaseDirectory,
            $"agent-{phase}",
            _configuration.Environment,
            _configuration.PromptViaStandardInput ? prompt : null,
            token);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Agent {phase} failed with exit code {result.ExitCode}. See {result.StandardErrorPath}.");
        }

        return File.Exists(responsePath)
            ? await File.ReadAllTextAsync(responsePath, token)
            : result.StandardOutput;
    }

    private string ReplacePlaceholders(string value, string promptPath, string responsePath)
        => value
            .Replace("{promptPath}", promptPath, StringComparison.Ordinal)
            .Replace("{responsePath}", responsePath, StringComparison.Ordinal)
            .Replace("{workspaceRoot}", _workspaceRoot, StringComparison.Ordinal)
            .Replace("{runRoot}", _runRoot, StringComparison.Ordinal);

    private static string ExtractJsonObject(string value)
    {
        int first = value.IndexOf('{');
        int last = value.LastIndexOf('}');
        if (first < 0 || last < first)
            throw new InvalidDataException("Agent response did not contain a JSON object.");
        return value[first..(last + 1)];
    }
}
