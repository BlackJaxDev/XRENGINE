namespace XREngine.LocalAgentBroker;

/// <summary>
/// Process configuration sourced from command-line paths and environment variable names.
/// </summary>
internal sealed record BrokerConfiguration
{
    public required string RepositoryRoot { get; init; }

    public string ApiKeyEnvironmentVariable { get; init; } = "OPENAI_API_KEY";

    public string? EditorAuthTokenEnvironmentVariable { get; init; }

    public int MaximumRetainedRuns { get; init; } = 32;

    public int MaximumConcurrentRuns { get; init; } = 4;

    public int RetentionMinutes { get; init; } = 120;

    public BrokerTraceMode TraceMode { get; init; }

    public static BrokerConfiguration Parse(string[] args)
    {
        string? repositoryRoot = null;
        string apiKeyEnvironmentVariable =
            Environment.GetEnvironmentVariable("XRE_LOCAL_AGENT_BROKER_API_KEY_ENV") ?? "OPENAI_API_KEY";
        string? editorAuthEnvironmentVariable =
            Environment.GetEnvironmentVariable("XRE_LOCAL_AGENT_BROKER_EDITOR_AUTH_ENV");

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (string.Equals(argument, "--repo-root", StringComparison.OrdinalIgnoreCase))
                repositoryRoot = ReadValue(args, ref index, argument);
            else if (string.Equals(argument, "--api-key-env", StringComparison.OrdinalIgnoreCase))
                apiKeyEnvironmentVariable = ReadValue(args, ref index, argument);
            else if (string.Equals(argument, "--editor-auth-env", StringComparison.OrdinalIgnoreCase))
                editorAuthEnvironmentVariable = ReadValue(args, ref index, argument);
            else
                throw new ArgumentException($"Unknown broker argument '{argument}'.");
        }

        repositoryRoot ??= Environment.GetEnvironmentVariable("XRE_LOCAL_AGENT_BROKER_REPO_ROOT");
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            throw new ArgumentException("--repo-root is required.");

        string fullRepositoryRoot = Path.GetFullPath(repositoryRoot);
        if (!File.Exists(Path.Combine(fullRepositoryRoot, "AGENTS.md")))
            throw new ArgumentException($"Repository root '{fullRepositoryRoot}' does not contain AGENTS.md.");

        return new BrokerConfiguration
        {
            RepositoryRoot = fullRepositoryRoot,
            ApiKeyEnvironmentVariable = ValidateEnvironmentVariableName(apiKeyEnvironmentVariable),
            EditorAuthTokenEnvironmentVariable = string.IsNullOrWhiteSpace(editorAuthEnvironmentVariable)
                ? null
                : ValidateEnvironmentVariableName(editorAuthEnvironmentVariable),
            MaximumRetainedRuns = ReadBoundedInteger("XRE_LOCAL_AGENT_BROKER_MAX_RUNS", 32, 4, 256),
            MaximumConcurrentRuns = ReadBoundedInteger("XRE_LOCAL_AGENT_BROKER_MAX_CONCURRENCY", 4, 1, 8),
            RetentionMinutes = ReadBoundedInteger("XRE_LOCAL_AGENT_BROKER_RETENTION_MINUTES", 120, 1, 1_440),
            TraceMode = ParseTraceMode(Environment.GetEnvironmentVariable("XRE_LOCAL_AGENT_BROKER_TRACE")),
        };
    }

    public string ReadApiKey()
        => Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable) ?? string.Empty;

    public string? ReadEditorAuthToken()
        => EditorAuthTokenEnvironmentVariable is null
            ? null
            : Environment.GetEnvironmentVariable(EditorAuthTokenEnvironmentVariable);

    private static string ReadValue(string[] args, ref int index, string argumentName)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"{argumentName} requires a value.");
        return args[index];
    }

    private static string ValidateEnvironmentVariableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Any(static character => character is '=' or '\0'))
        {
            throw new ArgumentException("Environment variable names must be non-empty and cannot contain '='.");
        }

        return name.Trim();
    }

    private static int ReadBoundedInteger(string name, int fallback, int minimum, int maximum)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!int.TryParse(value, out int parsed) || parsed < minimum || parsed > maximum)
            throw new ArgumentException($"{name} must be an integer between {minimum} and {maximum}.");
        return parsed;
    }

    private static BrokerTraceMode ParseTraceMode(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "off" or "false" or "0" => BrokerTraceMode.Off,
            "metadata" or "true" or "1" => BrokerTraceMode.Metadata,
            _ => throw new ArgumentException(
                "XRE_LOCAL_AGENT_BROKER_TRACE must be 'off' or 'metadata'."),
        };
}
