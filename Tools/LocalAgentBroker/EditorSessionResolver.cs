using System.Text.Json;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Resolves only explicitly named session manifests and accepts loopback endpoints.
/// </summary>
internal sealed class EditorSessionResolver(string repositoryRoot)
{
    private readonly string _sessionsRoot = Path.GetFullPath(
        Path.Combine(repositoryRoot, "Build", "_AgentValidation", "mcp-sessions"));

    public ResolvedEditorSession Resolve(string sessionName)
    {
        if (string.IsNullOrWhiteSpace(sessionName)
            || sessionName.Length > 64
            || !char.IsLetterOrDigit(sessionName[0])
            || sessionName.Any(static character =>
                !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException("Invalid editor session name.");
        }

        string sessionRoot = Path.GetFullPath(Path.Combine(_sessionsRoot, sessionName));
        string requiredPrefix = _sessionsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!sessionRoot.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Editor session path escaped the repository session root.");

        string manifestPath = Path.Combine(sessionRoot, "session.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Editor MCP session '{sessionName}' does not exist.", manifestPath);

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(manifestPath),
            new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        JsonElement root = document.RootElement;
        string manifestName = GetRequiredString(root, "name");
        if (!string.Equals(manifestName, sessionName, StringComparison.Ordinal))
            throw new InvalidDataException("Editor session manifest name does not match its directory.");

        string endpointText = GetRequiredString(root, "endpoint");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out Uri? endpoint)
            || !endpoint.IsLoopback
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidDataException(
                $"Editor MCP session '{sessionName}' endpoint must be loopback HTTP(S).");
        }

        return new ResolvedEditorSession
        {
            Name = sessionName,
            Endpoint = endpoint,
            ManifestPath = manifestPath,
        };
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Editor session manifest is missing '{propertyName}'.");
        }

        return value.GetString()!;
    }
}
