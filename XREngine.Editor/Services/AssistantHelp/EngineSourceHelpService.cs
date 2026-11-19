using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XREngine.Editor.Mcp;

namespace XREngine.Editor.Services.AssistantHelp;

/// <summary>
/// Reads bounded engine source or packaged help for the Assistant's explanation tools.
/// </summary>
public static class EngineSourceHelpService
{
    private const int MaximumFileBytes = 512 * 1024;
    private const int MaximumSearchBytes = 64 * 1024 * 1024;
    private const int MaximumSearchFiles = 5_000;
    private const int MaximumSearchDirectories = 10_000;
    private const int MaximumResults = 20;
    private const int MaximumReadLines = 120;
    private const int MaximumSnippetCharacters = 300;
    private static readonly string[] SourceExtensions = [".cs"];
    private static readonly string[] DocumentationExtensions = [".md"];

    /// <summary>Searches source and guide lines for a literal query within fixed file and output limits.</summary>
    public static Task<McpToolResponse> SearchAsync(string query, string scope, int maxResults, CancellationToken token)
        => Task.Run(() => Search(query, scope, maxResults, token), token);

    /// <summary>Reads numbered lines from one allowed file and hashes the same byte snapshot.</summary>
    public static Task<McpToolResponse> ReadAsync(string path, int startLine, int lineCount, CancellationToken token)
        => Task.Run(() => Read(path, startLine, lineCount, token), token);

    private static McpToolResponse Search(string query, string scope, int maxResults, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 128 || query.Any(char.IsControl))
            return new McpToolResponse("Enter a literal search term of 1 to 128 characters.", isError: true);
        if (scope is not ("all" or "source" or "docs"))
            return new McpToolResponse("Scope must be all, source, or docs.", isError: true);
        if (!TryFindHelpRoot(out string root, out string sourceKind, out string? revision,
                out string binaryMatch, out string reason))
            return new McpToolResponse(reason, isError: true);

        int resultLimit = Math.Clamp(maxResults, 1, MaximumResults);
        var matches = new List<object>(resultLimit);
        int consideredFiles = 0;
        int scannedFiles = 0;
        int scannedBytes = 0;
        int visitedDirectories = 0;
        int skippedDirectories = 0;
        bool truncated = false;
        foreach (string file in EnumerateAllowedFiles(root, scope, token, (visited, skipped) =>
        {
            visitedDirectories += visited;
            skippedDirectories += skipped;
            if (skipped > 0)
                truncated = true;
        }))
        {
            token.ThrowIfCancellationRequested();
            if (consideredFiles >= MaximumSearchFiles || scannedBytes >= MaximumSearchBytes)
            {
                truncated = true;
                break;
            }
            consideredFiles++;

            try
            {
                long size = new FileInfo(file).Length;
                if (size > MaximumFileBytes || size > MaximumSearchBytes - scannedBytes)
                    continue;
                byte[]? bytes = ReadBoundedFile(file, Math.Min(MaximumFileBytes, MaximumSearchBytes - scannedBytes));
                if (bytes is null)
                    continue;
                scannedFiles++;
                scannedBytes += bytes.Length;
                string relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
                using var lines = new StringReader(Encoding.UTF8.GetString(bytes));
                string? line;
                int lineNumber = 0;
                while ((line = lines.ReadLine()) is not null)
                {
                    token.ThrowIfCancellationRequested();
                    lineNumber++;
                    if (!line.Contains(query, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string excerpt = line.Trim();
                    if (excerpt.Length > MaximumSnippetCharacters)
                        excerpt = excerpt[..MaximumSnippetCharacters] + "…";
                    matches.Add(new
                    {
                        path = relativePath,
                        line = lineNumber,
                        excerpt,
                        matchKind = "literal_text"
                    });
                    if (matches.Count >= resultLimit)
                    {
                        truncated = true;
                        break;
                    }
                }
                if (matches.Count >= resultLimit)
                    break;
            }
            catch (IOException)
            {
                // A concurrent editor write can replace a source file during search.
            }
            catch (UnauthorizedAccessException)
            {
                // Search continues through accessible source files.
            }
        }

        return new McpToolResponse($"Found {matches.Count} literal text match(es).",
            new
            {
                query,
                scope,
                sourceKind,
                revision,
                binaryMatch,
                matches,
                consideredFiles,
                scannedFiles,
                scannedBytes,
                visitedDirectories,
                skippedDirectories,
                truncated,
                searchKind = "literal_text",
                note = "Text matches do not prove that a line defines or calls a symbol. Read the file before drawing that conclusion."
            });
    }

    private static McpToolResponse Read(string path, int startLine, int lineCount, CancellationToken token)
    {
        if (startLine < 1 || lineCount < 1 || lineCount > MaximumReadLines)
            return new McpToolResponse("Start line must be positive and line count must be 1 to 120.", isError: true);
        if (!TryFindHelpRoot(out string root, out string sourceKind, out string? revision,
                out string binaryMatch, out string reason))
            return new McpToolResponse(reason, isError: true);
        if (!TryResolveHelpPath(root, path, out string file, out reason))
            return new McpToolResponse(reason, isError: true);

        try
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (info.Length > MaximumFileBytes)
                return new McpToolResponse("This help file exceeds the 512 KiB read limit.", isError: true);
            byte[]? bytes = ReadBoundedFile(file, MaximumFileBytes);
            if (bytes is null)
                return new McpToolResponse("This help file exceeds the 512 KiB read limit.", isError: true);
            string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            using var reader = new StringReader(Encoding.UTF8.GetString(bytes));
            var lines = new List<object>(lineCount);
            string? line;
            int currentLine = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                token.ThrowIfCancellationRequested();
                currentLine++;
                if (currentLine < startLine)
                    continue;
                if (lines.Count == lineCount)
                    break;
                lines.Add(new { line = currentLine, text = line });
            }

            string relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            return new McpToolResponse($"Read {lines.Count} line(s) from {relativePath}.", new
            {
                path = relativePath,
                startLine,
                lineCount = lines.Count,
                lines,
                sha256,
                lastModifiedUtc = info.LastWriteTimeUtc.ToString("O"),
                sourceKind,
                revision,
                binaryMatch,
                note = binaryMatch == "unverified"
                    ? "This checkout was not matched to the running editor binary. The hash identifies only the returned file snapshot."
                    : "The packaged help manifest names this editor module ID. The manifest does not verify each source file against the running binary."
            });
        }
        catch (IOException ex)
        {
            return new McpToolResponse($"The help file could not be read: {ex.Message}", isError: true);
        }
        catch (UnauthorizedAccessException)
        {
            return new McpToolResponse("The help file is not accessible.", isError: true);
        }
    }

    private static byte[]? ReadBoundedFile(string file, int maximumBytes)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > maximumBytes)
            return null;
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static bool TryFindHelpRoot(out string root, out string sourceKind,
        out string? revision, out string binaryMatch, out string reason)
    {
        root = string.Empty;
        sourceKind = string.Empty;
        revision = null;
        binaryMatch = "unverified";
        reason = string.Empty;

        string bundle = Path.Combine(AppContext.BaseDirectory, "EngineHelp");
        string manifest = Path.Combine(bundle, "manifest.json");
        if (File.Exists(manifest))
        {
            try
            {
                if ((File.GetAttributes(bundle) & FileAttributes.ReparsePoint) != 0
                    || (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0)
                {
                    reason = "The packaged help path is a link and cannot be trusted.";
                    return false;
                }
                byte[]? manifestBytes = ReadBoundedFile(manifest, 4_096);
                if (manifestBytes is null)
                {
                    reason = "The packaged help manifest exceeds its size limit.";
                    return false;
                }
                using JsonDocument document = JsonDocument.Parse(manifestBytes);
                string? declaredId = document.RootElement.GetProperty("editorAssemblyMvid").GetString();
                string currentId = typeof(EngineSourceHelpService).Assembly.ManifestModule.ModuleVersionId.ToString("D");
                if (!string.Equals(declaredId, currentId, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "The packaged help manifest does not match this editor build.";
                    return false;
                }
                root = bundle;
                sourceKind = "bundle";
                binaryMatch = "manifestMatched";
                revision = document.RootElement.TryGetProperty("revision", out JsonElement value)
                    ? value.GetString() : null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                or KeyNotFoundException or InvalidOperationException)
            {
                reason = "The packaged help manifest is unavailable or invalid.";
                return false;
            }
        }

        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx"))
                || !File.Exists(Path.Combine(directory.FullName, "XREngine.Editor", "XREngine.Editor.csproj"))
                || !File.Exists(Path.Combine(directory.FullName, ".git"))
                    && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
                continue;
            root = directory.FullName;
            sourceKind = "checkout";
            revision = ReadCheckoutRevision(root);
            reason = "The source checkout was found but was not matched to the running editor binary.";
            return true;
        }

        reason = "Engine source help is unavailable for this editor build. No matching packaged help or source checkout was found.";
        return false;
    }

    private static string? ReadCheckoutRevision(string root)
    {
        try
        {
            string git = Path.Combine(root, ".git");
            if (!Directory.Exists(git))
                return null;
            string headPath = Path.Combine(git, "HEAD");
            if (!File.Exists(headPath) || new FileInfo(headPath).Length > 256)
                return null;
            string head = File.ReadAllText(headPath).Trim();
            if (!head.StartsWith("ref: ", StringComparison.Ordinal))
                return head.Length == 40 ? head : null;
            string reference = head[5..].Replace('/', Path.DirectorySeparatorChar);
            if (reference.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(reference))
                return null;
            string referencePath = Path.Combine(git, reference);
            if (!File.Exists(referencePath) || new FileInfo(referencePath).Length > 256)
                return null;
            string revision = File.ReadAllText(referencePath).Trim();
            return revision.Length == 40 ? revision : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateAllowedFiles(
        string root, string scope, CancellationToken token, Action<int, int> reportTraversal)
    {
        var roots = new List<(string Path, string[] Extensions)>();
        if (scope is "all" or "source")
        {
            if (Directory.Exists(Path.Combine(root, "src")))
                roots.Add((Path.Combine(root, "src"), SourceExtensions));
            foreach (string directory in EnumerateDirectoryEntries(root, "XREngine.*", true, token, reportTraversal))
                roots.Add((directory, SourceExtensions));
        }
        if (scope is "all" or "docs")
        {
            string docs = Path.Combine(root, "docs");
            if (Directory.Exists(docs))
                roots.Add((docs, DocumentationExtensions));
        }

        int visitedDirectories = 0;
        foreach ((string scanRoot, string[] extensions) in roots)
        {
            var pending = new Stack<string>();
            pending.Push(scanRoot);
            while (pending.TryPop(out string? directory))
            {
                token.ThrowIfCancellationRequested();
                if (++visitedDirectories > MaximumSearchDirectories)
                {
                    reportTraversal(0, 1);
                    yield break;
                }
                reportTraversal(1, 0);
                if (IsLinkOrUnavailable(directory, reportTraversal))
                    continue;
                foreach (string child in EnumerateDirectoryEntries(directory, "*", true, token, reportTraversal))
                {
                    token.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(child);
                    if (name is "bin" or "obj" or ".git" or "Build" or "node_modules")
                        continue;
                    pending.Push(child);
                }
                foreach (string file in EnumerateDirectoryEntries(directory, "*", false, token, reportTraversal))
                {
                    token.ThrowIfCancellationRequested();
                    if (IsLinkOrUnavailable(file, reportTraversal))
                        continue;
                    if (extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                        yield return file;
                }
            }
        }
    }

    private static IReadOnlyList<string> EnumerateDirectoryEntries(
        string directory, string pattern, bool directories, CancellationToken token,
        Action<int, int> reportTraversal)
    {
        var entries = new List<string>();
        try
        {
            IEnumerable<string> found = directories
                ? Directory.EnumerateDirectories(directory, pattern)
                : Directory.EnumerateFiles(directory, pattern);
            foreach (string entry in found)
            {
                token.ThrowIfCancellationRequested();
                if (entries.Count >= MaximumSearchDirectories)
                {
                    reportTraversal(0, 1);
                    break;
                }
                entries.Add(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reportTraversal(0, 1);
        }
        return entries;
    }

    private static bool IsLinkOrUnavailable(string path, Action<int, int> reportTraversal)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reportTraversal(0, 1);
            return true;
        }
    }

    private static bool TryResolveHelpPath(string root, string path, out string file, out string reason)
    {
        file = string.Empty;
        reason = "The help path is not allowed or does not exist.";
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.Any(char.IsControl)
            || Path.IsPathRooted(path) || path.Contains(':'))
            return false;
        string[] segments = path.Replace('\\', '/').Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            return false;
        if (segments[0] != "docs" && !segments[0].StartsWith("XREngine.", StringComparison.Ordinal)
            && segments[0] != "src")
            return false;
        string extension = Path.GetExtension(path);
        if (segments[0] == "docs" && !extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || segments[0] != "docs" && !extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            return false;

        string current = root;
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                return false;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                reason = "The help path crosses a link and cannot be read.";
                return false;
            }
        }
        if (!File.Exists(current))
            return false;
        file = current;
        reason = string.Empty;
        return true;
    }
}
