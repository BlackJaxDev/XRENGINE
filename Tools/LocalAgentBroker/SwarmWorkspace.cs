using System.Security.Cryptography;
using System.Text;
using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Captures swarm-owned files and applies explicitly approved, hash-bound text changes.
/// </summary>
internal sealed class SwarmWorkspace
{
    private const string MissingSha256 = "missing";
    private const int MaximumPatchFileBytes = 1_048_576;
    private readonly RepositoryPathPolicy _pathPolicy;
    private readonly RepositoryTextFileReader _reader;
    private readonly string _mutexName;

    public SwarmWorkspace(RepositoryPathPolicy pathPolicy)
    {
        _pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        _reader = new RepositoryTextFileReader(pathPolicy);
        _mutexName = $@"Local\XREngine.LocalAgentBroker.SwarmApply.{GetRepositoryHash(pathPolicy.RepositoryRoot)}";
    }

    /// <summary>
    /// Returns canonical, policy-approved paths the swarm may propose changes for.
    /// </summary>
    public IReadOnlyList<string> NormalizeAllowedPaths(AgentSwarmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AllowedPaths is null)
            throw new ArgumentException("swarm.allowed_paths cannot be null.", nameof(options));
        if (options.AllowedPaths.Count > 64)
            throw new ArgumentException("swarm.allowed_paths cannot exceed 64 paths.", nameof(options));

        var paths = new List<string>(options.AllowedPaths.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in options.AllowedPaths)
        {
            string fullPath = _pathPolicy.ResolvePotentialTextFile(path);
            if (!File.Exists(fullPath))
                EnsureExistingParentDirectory(fullPath, path);
            string canonical = _pathPolicy.ToRelativePath(fullPath);
            if (!seen.Add(canonical))
                throw new ArgumentException($"swarm.allowed_paths contains duplicate path '{canonical}'.", nameof(options));
            paths.Add(canonical);
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;
    }

    /// <summary>
    /// Captures normal request context together with full snapshots for swarm-owned paths.
    /// An allowed path that does not exist is represented by a stable missing sentinel.
    /// </summary>
    public IReadOnlyList<AgentContextFileSnapshot> Capture(AgentRunRequest request, AgentSwarmOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request.ContextFiles);
        ArgumentNullException.ThrowIfNull(request.Budget);

        IReadOnlyList<string> allowedPaths = NormalizeAllowedPaths(options);
        var normalFiles = new Dictionary<string, AgentContextFileRequest>(StringComparer.OrdinalIgnoreCase);
        foreach (AgentContextFileRequest contextFile in request.ContextFiles)
        {
            string canonical = _pathPolicy.ToRelativePath(_pathPolicy.ResolveTextFile(contextFile.Path));
            if (!normalFiles.TryAdd(canonical, contextFile))
                throw new ArgumentException($"context_files contains duplicate path '{canonical}'.");
        }

        var allPaths = new HashSet<string>(normalFiles.Keys, StringComparer.OrdinalIgnoreCase);
        allPaths.UnionWith(allowedPaths);
        if (allPaths.Count > 64 || allPaths.Count > request.Budget.MaxContextFiles)
            throw new ArgumentException("Combined context_files and swarm.allowed_paths exceeds the context file budget.");

        var snapshots = new List<AgentContextFileSnapshot>(allPaths.Count);
        long totalRawBytes = 0;
        long totalRenderedBytes = 0;
        foreach (string canonical in allPaths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            bool isAllowedPath = allowedPaths.Contains(canonical, StringComparer.OrdinalIgnoreCase);
            string fullPath = isAllowedPath
                ? _pathPolicy.ResolvePotentialTextFile(canonical)
                : _pathPolicy.ResolveTextFile(canonical);
            AgentContextFileSnapshot snapshot;
            if (!File.Exists(fullPath))
            {
                snapshot = new AgentContextFileSnapshot
                {
                    Path = canonical,
                    Sha256 = MissingSha256,
                    Content = string.Empty,
                };
            }
            else if (isAllowedPath)
            {
                string? expectedSha256 = normalFiles.TryGetValue(canonical, out AgentContextFileRequest? contextFile)
                    ? contextFile.ExpectedSha256
                    : null;
                snapshot = _reader.Read(
                    fullPath,
                    canonical,
                    request.Budget.MaxContextFileBytes,
                    expectedSha256: expectedSha256);
            }
            else
            {
                AgentContextFileRequest contextFile = normalFiles[canonical];
                snapshot = _reader.Read(
                    fullPath,
                    canonical,
                    request.Budget.MaxContextFileBytes,
                    contextFile.StartLine,
                    contextFile.EndLine,
                    contextFile.ExpectedSha256);
            }

            totalRawBytes += snapshot.RawByteLength;
            if (totalRawBytes > request.Budget.MaxContextBytes)
                throw new ArgumentException("Combined context exceeds budget.max_context_bytes.");
            totalRenderedBytes += AgentContextFileInputBuilder.GetRenderedByteCount(snapshot);
            if (totalRenderedBytes > request.Budget.MaxContextRenderedBytes)
                throw new ArgumentException("Combined context exceeds budget.max_context_rendered_bytes.");
            snapshots.Add(snapshot);
        }

        return snapshots;
    }

    /// <summary>
    /// Applies exact reviewed replacements after every proposal and snapshot has passed preflight.
    /// This method serializes broker writes for this repository but cannot make changes transactional
    /// across crashes or processes that do not participate in the mutex.
    /// </summary>
    public SwarmApplyResult Apply(
        IReadOnlyList<AgentSwarmCodeChange> changes,
        IReadOnlyList<AgentContextFileSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(changes);
            ArgumentNullException.ThrowIfNull(snapshots);
            List<PreparedChange> prepared = PrepareChanges(changes, snapshots);
            if (prepared.Count == 0)
                return new SwarmApplyResult { Success = true };

            using var mutex = new Mutex(false, _mutexName);
            cancellationToken.ThrowIfCancellationRequested();
            bool mutexHeld;
            try
            {
                int waitResult = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], TimeSpan.FromSeconds(30));
                if (waitResult == 1)
                    throw new OperationCanceledException(cancellationToken);
                mutexHeld = waitResult == 0;
            }
            catch (AbandonedMutexException exception)
            {
                if (exception.MutexIndex != 0)
                    throw;
                mutexHeld = true;
            }
            if (!mutexHeld)
                return Failed([], "Timed out waiting for another broker patch application.");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (PreparedChange change in prepared)
                    PreflightCurrentState(change);

                var applied = new List<AppliedChange>(prepared.Count);
                try
                {
                    foreach (PreparedChange change in prepared)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ApplyOne(change, applied);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    return Failed(RollBack(applied), exception.Message, exception is OperationCanceledException);
                }

                return new SwarmApplyResult
                {
                    Success = true,
                    AppliedPaths = applied.Select(static change => change.Path).ToArray(),
                };
            }
            finally
            {
                if (mutexHeld)
                    mutex.ReleaseMutex();
            }
        }
        catch (OperationCanceledException)
        {
            return Failed([], "Patch application was cancelled.", cancelled: true);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Failed([], exception.Message);
        }
    }

    private List<PreparedChange> PrepareChanges(
        IReadOnlyList<AgentSwarmCodeChange> changes,
        IReadOnlyList<AgentContextFileSnapshot> snapshots)
    {
        IReadOnlyList<AgentSwarmCodeChange> mergedChanges = AgentSwarmChangeMerger.Merge(changes, snapshots);
        var snapshotByPath = new Dictionary<string, AgentContextFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (AgentContextFileSnapshot snapshot in snapshots)
        {
            string canonical = _pathPolicy.ToRelativePath(_pathPolicy.ResolvePotentialTextFile(snapshot.Path));
            if (!snapshotByPath.TryAdd(canonical, snapshot))
                throw new ArgumentException($"Snapshots contain duplicate path '{canonical}'.");
        }

        var prepared = new List<PreparedChange>(mergedChanges.Count);
        var changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AgentSwarmCodeChange change in mergedChanges)
        {
            ArgumentNullException.ThrowIfNull(change);
            string canonical = _pathPolicy.ToRelativePath(_pathPolicy.ResolvePotentialTextFile(change.Path));
            if (!changedPaths.Add(canonical))
                throw new ArgumentException($"Changes contain duplicate path '{canonical}'.");
            if (!snapshotByPath.TryGetValue(canonical, out AgentContextFileSnapshot? snapshot))
                throw new ArgumentException($"Change path '{canonical}' was not captured for review.");
            if (!string.Equals(change.BaseSha256, snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Change path '{canonical}' does not match its captured base hash.");

            bool isNewFile = string.Equals(snapshot.Sha256, MissingSha256, StringComparison.Ordinal);
            if (isNewFile)
            {
                if (change.OldText.Length != 0)
                    throw new ArgumentException($"New file '{canonical}' must use empty old_text.");
            }
            else
            {
                if (!string.Equals(change.OldText, snapshot.Content, StringComparison.Ordinal))
                    throw new ArgumentException($"Merged change for '{canonical}' does not replace the complete captured file.");
            }

            string fullPath = _pathPolicy.ResolvePotentialTextFile(canonical);
            prepared.Add(new PreparedChange(canonical, fullPath, snapshot, change, isNewFile));
        }

        return prepared;
    }

    private void PreflightCurrentState(PreparedChange change)
    {
        _ = _pathPolicy.ResolvePotentialTextFile(change.Path);
        if (change.IsNewFile)
        {
            if (File.Exists(change.FullPath))
                throw new IOException($"New file '{change.Path}' now exists.");
            EnsureExistingParentDirectory(change.FullPath, change.Path);
            return;
        }

        byte[] rawBytes = ReadRawExistingFile(change.FullPath, change.Path, out _);
        EnsureHash(rawBytes, change.Snapshot.Sha256, change.Path);
    }

    private void ApplyOne(PreparedChange change, ICollection<AppliedChange> applied)
    {
        _ = _pathPolicy.ResolvePotentialTextFile(change.Path);
        if (change.IsNewFile)
        {
            EnsureExistingParentDirectory(change.FullPath, change.Path);
            byte[] newFileOutput = new UTF8Encoding(false, true).GetBytes(change.Change.NewText);
            EnsurePatchSize(newFileOutput, change.Path);
            using (var createStream = new FileStream(change.FullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, FileOptions.WriteThrough))
            {
                applied.Add(new AppliedChange(change.Path, change.FullPath, null, newFileOutput, IsNewFile: true));
                SwarmFileLinkInspector.ValidateOpenedFile(createStream.SafeFileHandle, change.FullPath, change.Path);
                createStream.Write(newFileOutput);
                createStream.Flush(flushToDisk: true);
            }
            VerifyRawHash(change.FullPath, newFileOutput, change.Path);
            return;
        }

        using var stream = new FileStream(change.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 16_384, FileOptions.WriteThrough);
        _ = _pathPolicy.ResolvePotentialTextFile(change.Path);
        SwarmFileLinkInspector.ValidateOpenedFile(stream.SafeFileHandle, change.FullPath, change.Path);
        byte[] original = ReadAllBytes(stream);
        EnsureHash(original, change.Snapshot.Sha256, change.Path);
        string originalText = DecodeUtf8(original, change.Path, out bool hasBom, out string newLine);
        if (!string.Equals(originalText, change.Change.OldText, StringComparison.Ordinal))
            throw new IOException($"Repository file '{change.Path}' text changed since swarm capture.");
        byte[] output = EncodeUtf8(change.Change.NewText, hasBom, newLine);
        EnsurePatchSize(output, change.Path);
        var appliedChange = new AppliedChange(change.Path, change.FullPath, original, output, IsNewFile: false);
        applied.Add(appliedChange);
        try
        {
            stream.Position = 0;
            stream.SetLength(0);
            stream.Write(output);
            stream.Flush(flushToDisk: true);
            stream.Position = 0;
            EnsureHash(ReadAllBytes(stream), Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant(), change.Path);
        }
        catch
        {
            // A partial write belongs to us while this exclusive handle remains open.
            // Restore it now; after close, only a complete-output hash permits rollback.
            try
            {
                stream.Position = 0;
                stream.SetLength(0);
                stream.Write(original);
                stream.Flush(flushToDisk: true);
                EnsureHash(ReadAllBytes(stream), change.Snapshot.Sha256, change.Path);
                applied.Remove(appliedChange);
            }
            catch (Exception)
            {
                // Keep the path in the residual list if even restoring the original fails.
            }
            throw;
        }
    }

    private IReadOnlyList<string> RollBack(IReadOnlyList<AppliedChange> applied)
    {
        var remainingAppliedPaths = new HashSet<string>(
            applied.Select(static change => change.Path),
            StringComparer.OrdinalIgnoreCase);
        for (int index = applied.Count - 1; index >= 0; index--)
        {
            AppliedChange change = applied[index];
            try
            {
                if (change.IsNewFile)
                {
                    // Do not delete by a pathname after releasing its ownership handle.
                    // Retain and report new files for caller inspection on batch failure.
                    continue;
                }

                using var rollbackStream = new FileStream(change.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                _ = _pathPolicy.ResolvePotentialTextFile(change.Path);
                SwarmFileLinkInspector.ValidateOpenedFile(rollbackStream.SafeFileHandle, change.FullPath, change.Path);
                if (!SHA256.HashData(ReadAllBytes(rollbackStream)).AsSpan().SequenceEqual(SHA256.HashData(change.Output)))
                    continue;
                rollbackStream.Position = 0;
                rollbackStream.SetLength(0);
                rollbackStream.Write(change.Original!);
                rollbackStream.Flush(flushToDisk: true);
                EnsureHash(ReadAllBytes(rollbackStream), Convert.ToHexString(SHA256.HashData(change.Original!)), change.Path);
                remainingAppliedPaths.Remove(change.Path);
            }
            catch (Exception)
            {
                // The returned partial path list makes a failed rollback explicit to the coordinator.
            }
        }
        return remainingAppliedPaths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static SwarmApplyResult Failed(IEnumerable<string> appliedPaths, string failure, bool cancelled = false)
        => new()
        {
            AppliedPaths = appliedPaths.ToArray(),
            Failure = failure,
            Cancelled = cancelled,
        };

    private static byte[] ReadRawExistingFile(string fullPath, string path, out bool hasBom)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        SwarmFileLinkInspector.RejectHardLink(stream.SafeFileHandle, path);
        byte[] raw = ReadAllBytes(stream);
        _ = DecodeUtf8(raw, path, out hasBom, out _);
        return raw;
    }

    private static byte[] ReadAllBytes(FileStream stream)
    {
        if (stream.Length > MaximumPatchFileBytes)
            throw new IOException("File exceeds supported patch size.");
        stream.Position = 0;
        byte[] bytes = new byte[(int)stream.Length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                throw new IOException("File changed while being read.");
            offset += read;
        }
        return bytes;
    }

    private static string DecodeUtf8(byte[] bytes, string path, out bool hasBom, out string newLine)
    {
        hasBom = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        ReadOnlySpan<byte> textBytes = hasBom ? bytes.AsSpan(3) : bytes;
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(textBytes);
        }
        catch (DecoderFallbackException)
        {
            throw new IOException($"Repository file '{path}' is not strict UTF-8 text.");
        }
        if (text.Contains('\0'))
            throw new IOException($"Repository file '{path}' appears to be binary.");
        string withoutCrLf = text.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        bool hasCrLf = text.Contains("\r\n", StringComparison.Ordinal);
        bool hasLf = withoutCrLf.Contains('\n');
        bool hasCr = withoutCrLf.Contains('\r');
        if ((hasCrLf ? 1 : 0) + (hasLf ? 1 : 0) + (hasCr ? 1 : 0) > 1)
            throw new IOException($"Repository file '{path}' uses mixed newline styles and cannot be auto-applied.");
        newLine = hasCrLf ? "\r\n" : hasCr ? "\r" : "\n";
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static byte[] EncodeUtf8(string normalizedText, bool hasBom, string newLine)
    {
        string text = normalizedText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (newLine != "\n")
            text = text.Replace("\n", newLine, StringComparison.Ordinal);
        byte[] textBytes = new UTF8Encoding(false, true).GetBytes(text);
        if (!hasBom)
            return textBytes;
        byte[] output = new byte[textBytes.Length + 3];
        output[0] = 0xEF;
        output[1] = 0xBB;
        output[2] = 0xBF;
        textBytes.CopyTo(output, 3);
        return output;
    }

    private static void EnsureHash(byte[] bytes, string expectedSha256, string path)
    {
        string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Repository file '{path}' changed since swarm capture.");
    }

    private static void VerifyRawHash(string fullPath, byte[] expected, string path)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!SHA256.HashData(ReadAllBytes(stream)).AsSpan().SequenceEqual(SHA256.HashData(expected)))
            throw new IOException($"Repository file '{path}' did not match its written contents.");
    }

    private static void EnsurePatchSize(byte[] bytes, string path)
    {
        if (bytes.Length > MaximumPatchFileBytes)
            throw new IOException($"Patched file '{path}' exceeds the {MaximumPatchFileBytes}-byte limit.");
    }

    private void EnsureExistingParentDirectory(string fullPath, string path)
    {
        string? parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            throw new IOException($"Parent directory for '{path}' does not exist.");
        _ = _pathPolicy.ResolveDirectory(_pathPolicy.ToRelativePath(parent));
    }

    private static string GetRepositoryHash(string repositoryRoot)
    {
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot))
            .Replace('/', '\\')
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..16];
    }

}
