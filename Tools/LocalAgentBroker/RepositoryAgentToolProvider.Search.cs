using System.IO.Enumeration;
using System.Text.Json;
using System.Text.Json.Nodes;
using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

internal sealed partial class RepositoryAgentToolProvider
{
    private AgentToolResult Search(JsonElement arguments, CancellationToken cancellationToken)
    {
        string query = RequiredString(arguments, "query");
        if (query.Length is < 2 or > MaximumQueryCharacters)
            throw new ArgumentException("query must contain between 2 and 256 characters.");

        string? pathPrefix = OptionalString(arguments, "path_prefix");
        bool caseSensitive = OptionalBoolean(arguments, "case_sensitive", false);
        int maxResults = OptionalInt32(
            arguments,
            "max_results",
            25,
            1,
            MaximumSearchResults);
        IReadOnlyList<string> globs = ReadGlobs(arguments);
        StringComparison comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        IReadOnlyList<string> searchRoots = ResolveSearchRoots(pathPrefix);

        var matches = new JsonArray();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int filesScanned = 0;
        long bytesScanned = 0;
        bool truncated = false;
        string truncationReason = string.Empty;
        foreach (string fullPath in EnumerateFilesStable(searchRoots, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_remainingSearchBytes <= 0)
            {
                truncated = true;
                truncationReason = "run_byte_scan_limit";
                break;
            }
            string relativePath = _pathPolicy.ToRelativePath(fullPath);
            if (!seenFiles.Add(relativePath) || !MatchesAnyGlob(relativePath, globs))
                continue;
            if (filesScanned >= MaximumSearchFiles)
            {
                truncated = true;
                truncationReason = "file_scan_limit";
                break;
            }

            AgentContextFileSnapshot snapshot;
            try
            {
                snapshot = _reader.Read(
                    fullPath,
                    relativePath,
                    MaximumReadableFileBytes);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (bytesScanned + snapshot.RawByteLength > MaximumSearchBytes
                || snapshot.RawByteLength > _remainingSearchBytes)
            {
                truncated = true;
                truncationReason = snapshot.RawByteLength > _remainingSearchBytes
                    ? "run_byte_scan_limit"
                    : "byte_scan_limit";
                break;
            }
            filesScanned++;
            bytesScanned += snapshot.RawByteLength;
            _remainingSearchBytes -= snapshot.RawByteLength;

            int pathColumn = relativePath.IndexOf(query, comparison);
            if (pathColumn >= 0)
            {
                matches.Add(CreateMatch(relativePath, 0, pathColumn + 1, relativePath, snapshot.Sha256));
                if (matches.Count >= maxResults)
                {
                    truncated = true;
                    truncationReason = "result_limit";
                    break;
                }
            }

            string[] lines = snapshot.Content.Split('\n');
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                int column = lines[lineIndex].IndexOf(query, comparison);
                if (column < 0)
                    continue;
                matches.Add(CreateMatch(
                    relativePath,
                    lineIndex + 1,
                    column + 1,
                    BuildSnippet(lines[lineIndex], column, query.Length),
                    snapshot.Sha256));
                if (matches.Count < maxResults)
                    continue;
                truncated = true;
                truncationReason = "result_limit";
                break;
            }
            if (matches.Count >= maxResults)
                break;
        }

        var payload = new JsonObject
        {
            ["query"] = query,
            ["matches"] = matches,
            ["truncated"] = truncated,
            ["truncation_reason"] = truncationReason,
            ["files_scanned"] = filesScanned,
            ["bytes_scanned"] = bytesScanned,
        };
        return BoundJsonPayload(payload);
    }

    private IEnumerable<string> EnumerateFilesStable(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken)
    {
        foreach (string root in roots.OrderBy(_pathPolicy.ToRelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                string[] files;
                string[] directories;
                try
                {
                    files = Directory.GetFiles(directory);
                    directories = Directory.GetDirectories(directory);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    if (_pathPolicy.IsTextFileCandidate(file))
                        yield return file;
                }

                Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
                for (int index = directories.Length - 1; index >= 0; index--)
                {
                    string child = directories[index];
                    try
                    {
                        if (_pathPolicy.IsDirectoryTraversalAllowed(child))
                            pending.Push(child);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
    }

    private IReadOnlyList<string> ResolveSearchRoots(string? pathPrefix)
    {
        if (string.IsNullOrWhiteSpace(pathPrefix))
            return _allowedRoots;

        string fullPath = _pathPolicy.ResolveDirectory(pathPrefix);
        if (!_allowedRoots.Any(root => IsSameOrDescendant(fullPath, root)))
            throw new ArgumentException($"Repository path_prefix '{pathPrefix}' is outside the authorized roots.");
        return [fullPath];
    }

    private static JsonObject CreateMatch(
        string path,
        int line,
        int column,
        string snippet,
        string sha256)
        => new()
        {
            ["path"] = path,
            ["line"] = line,
            ["column"] = column,
            ["snippet"] = snippet,
            ["sha256"] = sha256,
        };

    private static string BuildSnippet(string line, int matchIndex, int matchLength)
    {
        const int maximumSnippetLength = 240;
        if (line.Length <= maximumSnippetLength)
            return line;
        int start = Math.Max(0, matchIndex - 80);
        int end = Math.Min(line.Length, Math.Max(start + maximumSnippetLength, matchIndex + matchLength));
        start = Math.Max(0, end - maximumSnippetLength);
        return (start > 0 ? "…" : string.Empty)
            + line[start..end]
            + (end < line.Length ? "…" : string.Empty);
    }

    private static bool MatchesAnyGlob(string relativePath, IReadOnlyList<string> globs)
    {
        if (globs.Count == 0)
            return true;
        foreach (string glob in globs)
        {
            string candidate = glob.Contains('/') || glob.Contains('\\')
                ? relativePath
                : Path.GetFileName(relativePath);
            if (FileSystemName.MatchesSimpleExpression(
                glob.Replace('\\', '/'),
                candidate.Replace('\\', '/'),
                ignoreCase: true))
            {
                return true;
            }
        }
        return false;
    }

    private static IReadOnlyList<string> ReadGlobs(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("include_globs", out JsonElement value))
            return [];
        if (value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("include_globs must be an array.");
        if (value.GetArrayLength() > MaximumGlobCount)
            throw new ArgumentException("include_globs cannot contain more than 8 entries.");

        var globs = new List<string>();
        foreach (JsonElement element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
                throw new ArgumentException("include_globs entries must be strings.");
            string glob = element.GetString() ?? string.Empty;
            if (glob.Length is < 1 or > 128
                || Path.IsPathRooted(glob)
                || glob.Contains("..", StringComparison.Ordinal)
                || glob.Contains(':', StringComparison.Ordinal))
            {
                throw new ArgumentException("include_globs contains an invalid repository-relative pattern.");
            }
            globs.Add(glob);
        }
        return globs;
    }

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        string normalizedCandidate = Path.GetFullPath(candidate);
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
