using System.Text.Json;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Resolves only explicitly named session manifests and accepts loopback endpoints.
/// </summary>
internal sealed class EditorSessionResolver(string repositoryRoot)
{
    private readonly string _sessionsRoot = Path.GetFullPath(
        Path.Combine(
            repositoryRoot,
            "Build",
            "_AgentValidation",
            "00000000-000000-shared",
            "mcp-sessions"));

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

        string requiredPrefix = _sessionsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string? manifestPath = Directory.Exists(_sessionsRoot)
            ? Directory.EnumerateFiles(_sessionsRoot, "session.json", SearchOption.AllDirectories)
                .Where(path => Path.GetFullPath(path).StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(path => ManifestNamesSession(path, sessionName))
            : null;
        if (manifestPath is null)
            throw new FileNotFoundException($"Editor MCP session '{sessionName}' does not exist.");

        using JsonDocument document = ParseManifest(manifestPath);
        JsonElement root = document.RootElement;
        string manifestName = GetRequiredString(root, "name");
        if (!string.Equals(manifestName, sessionName, StringComparison.Ordinal))
            throw new InvalidDataException("Editor session manifest name does not match the requested session.");

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

    private static bool ManifestNamesSession(string manifestPath, string sessionName)
    {
        try
        {
            using JsonDocument document = ParseManifest(manifestPath);
            return string.Equals(
                GetRequiredString(document.RootElement, "name"),
                sessionName,
                StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static JsonDocument ParseManifest(string manifestPath)
        => JsonDocument.Parse(
            File.ReadAllText(manifestPath),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });

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
