using System.Security.Cryptography;

namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Enforces edit scope and restores rejected or policy-violating LLM attempts.
/// </summary>
public sealed class SelfIterationWorkspaceManager
{
    private readonly string _workspaceRoot;
    private readonly string[] _allowedPrefixes;
    private readonly string[] _managedPaths;
    private readonly SelfIterationProcessRunner _processRunner;
    private int _gitInvocation;

    public SelfIterationWorkspaceManager(
        string workspaceRoot,
        IEnumerable<string> allowedPrefixes,
        IEnumerable<string> managedPaths,
        SelfIterationProcessRunner processRunner)
    {
        _workspaceRoot = workspaceRoot;
        _allowedPrefixes = allowedPrefixes
            .Select(SelfIterationConfiguration.NormalizeRelativePath)
            .ToArray();
        _managedPaths = managedPaths
            .Select(SelfIterationConfiguration.NormalizeRelativePath)
            .ToArray();
        _processRunner = processRunner;
    }

    public async Task EnsureCleanTrackedWorktreeAsync(
        string evidenceDirectory,
        CancellationToken token)
    {
        SelfIterationProcessResult worktree = await RunGitAsync(
            ["diff", "--quiet", "--ignore-submodules=dirty"],
            evidenceDirectory,
            "clean-worktree",
            token);
        SelfIterationProcessResult index = await RunGitAsync(
            ["diff", "--cached", "--quiet", "--ignore-submodules=dirty"],
            evidenceDirectory,
            "clean-index",
            token);
        if (worktree.ExitCode != 0 || index.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "The self-iteration loop requires a clean tracked worktree. " +
                "Commit or stash current tracked changes before granting an LLM write access.");
        }

        HashSet<string> untracked = await GetUntrackedFilesAsync(
            evidenceDirectory,
            "clean-untracked",
            token);
        string[] unprotected = untracked
            .Where(path => !IsGeneratedPath(path) && !IsAllowedPath(path) && !IsManagedPath(path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unprotected.Length > 0)
        {
            throw new InvalidOperationException(
                "The self-iteration loop found non-generated untracked files outside its protected " +
                $"edit/document scopes: {string.Join(", ", unprotected)}. Commit, move, or remove them first.");
        }
    }

    public async Task<SelfIterationWorkspaceCheckpoint> CaptureAsync(
        string checkpointDirectory,
        CancellationToken token)
    {
        Directory.CreateDirectory(checkpointDirectory);
        SelfIterationProcessResult staged = await RunGitAsync(
            ["diff", "--cached", "--quiet", "--ignore-submodules=dirty"],
            checkpointDirectory,
            "checkpoint-index",
            token);
        if (staged.ExitCode != 0)
            throw new InvalidOperationException("Staged files are not supported while the self-iteration loop is active.");

        string headCommit = await ReadGitValueAsync(
            ["rev-parse", "--verify", "HEAD"],
            checkpointDirectory,
            "checkpoint-head",
            token);
        string headReference = await ReadGitValueAsync(
            ["rev-parse", "--abbrev-ref", "HEAD"],
            checkpointDirectory,
            "checkpoint-head-reference",
            token);
        string patchPath = Path.Combine(checkpointDirectory, "before.patch");
        SelfIterationProcessResult patch = await RunGitAsync(
            ["diff", "--binary", "--full-index", $"--output={patchPath}", "HEAD", "--", "."],
            checkpointDirectory,
            "checkpoint-patch",
            token);
        if (!patch.Succeeded)
            throw new InvalidOperationException($"Could not create workspace checkpoint: {patch.StandardError}");

        HashSet<string> trackedDiffPaths = await GetGitPathSetAsync(
            ["diff", "--name-only", "HEAD", "--", "."],
            checkpointDirectory,
            "checkpoint-diff-paths",
            token);
        HashSet<string> normalStatus = await GetGitLineSetAsync(
            ["status", "--porcelain=v1", "--untracked-files=normal", "--ignore-submodules=dirty"],
            checkpointDirectory,
            "checkpoint-status",
            token);
        HashSet<string> allUntracked = await GetUntrackedFilesAsync(
            checkpointDirectory,
            "checkpoint-all-untracked",
            token);
        HashSet<string> backedUpUntracked = allUntracked
            .Where(path => !IsGeneratedPath(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> hashes = CaptureWatchedHashes(
            trackedDiffPaths.Concat(allUntracked));
        BackupUntrackedFiles(backedUpUntracked, checkpointDirectory);

        return new SelfIterationWorkspaceCheckpoint
        {
            Directory = checkpointDirectory,
            PatchPath = patchPath,
            HeadCommit = headCommit,
            HeadReference = headReference,
            WatchedFileHashes = hashes,
            TrackedDiffPaths = trackedDiffPaths,
            NormalStatusEntries = normalStatus,
            UntrackedFiles = allUntracked,
            BackedUpUntrackedFiles = backedUpUntracked,
        };
    }

    public async Task<IReadOnlyList<string>> GetChangedPathsAsync(
        SelfIterationWorkspaceCheckpoint checkpoint,
        CancellationToken token)
    {
        await EnsureHeadUnchangedAsync(checkpoint, "after", token);
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> currentHashes = CaptureWatchedHashes(
            checkpoint.WatchedFileHashes.Keys);
        foreach (string path in checkpoint.WatchedFileHashes.Keys.Union(currentHashes.Keys))
        {
            checkpoint.WatchedFileHashes.TryGetValue(path, out string? before);
            currentHashes.TryGetValue(path, out string? after);
            if (!string.Equals(before, after, StringComparison.Ordinal))
                changed.Add(path);
        }

        HashSet<string> trackedPaths = await GetGitPathSetAsync(
            ["diff", "--name-only", "HEAD", "--", "."],
            checkpoint.Directory,
            "after-diff-paths",
            token);
        foreach (string path in trackedPaths.Except(
                     checkpoint.TrackedDiffPaths,
                     StringComparer.OrdinalIgnoreCase))
        {
            changed.Add(path);
        }

        HashSet<string> currentStatus = await GetGitLineSetAsync(
            ["status", "--porcelain=v1", "--untracked-files=normal", "--ignore-submodules=dirty"],
            checkpoint.Directory,
            "after-status",
            token);
        foreach (string entry in currentStatus.Except(checkpoint.NormalStatusEntries, StringComparer.Ordinal))
        {
            string path = ParsePorcelainPath(entry);
            if (!string.IsNullOrWhiteSpace(path))
                changed.Add(path);
        }
        return changed.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool IsAllowedPath(string path)
    {
        string normalized = SelfIterationConfiguration.NormalizeRelativePath(path);
        if (IsManagedPath(normalized))
            return false;
        return _allowedPrefixes.Any(prefix =>
            normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsManagedPath(string path)
    {
        string normalized = SelfIterationConfiguration.NormalizeRelativePath(path);
        return _managedPaths.Any(managed =>
            normalized.Equals(managed, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(managed.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
    }

    public async Task RestoreAsync(
        SelfIterationWorkspaceCheckpoint checkpoint,
        CancellationToken token)
    {
        await EnsureHeadUnchangedAsync(checkpoint, "restore", token);
        HashSet<string> stagedPaths = await GetGitPathSetAsync(
            ["diff", "--cached", "--name-only", "--", "."],
            checkpoint.Directory,
            "restore-staged-paths",
            token);
        if (stagedPaths.Count > 0)
        {
            await RunGitInChunksAsync(
                ["restore", "--staged", "--"],
                stagedPaths,
                checkpoint.Directory,
                "restore-index",
                token);
        }

        HashSet<string> trackedPaths = await GetGitPathSetAsync(
            ["diff", "--name-only", "HEAD", "--", "."],
            checkpoint.Directory,
            "restore-tracked-paths",
            token);
        if (trackedPaths.Count > 0)
        {
            await RunGitInChunksAsync(
                ["restore", "--worktree", "--"],
                trackedPaths,
                checkpoint.Directory,
                "restore-worktree",
                token);
        }

        HashSet<string> currentAllUntracked = await GetUntrackedFilesAsync(
            checkpoint.Directory,
            "restore-all-untracked",
            token);
        var newUntrackedDirectories = new List<string>();
        foreach (string relativePath in currentAllUntracked.Except(
                     checkpoint.UntrackedFiles,
                     StringComparer.OrdinalIgnoreCase))
        {
            string absolutePath = ResolveWorkspaceFile(relativePath);
            if (File.Exists(absolutePath))
                File.Delete(absolutePath);
            else if (Directory.Exists(absolutePath))
                newUntrackedDirectories.Add(relativePath);
        }
        RestoreUntrackedFiles(checkpoint);

        if (new FileInfo(checkpoint.PatchPath).Length > 0)
        {
            SelfIterationProcessResult apply = await RunGitAsync(
                ["apply", "--whitespace=nowarn", checkpoint.PatchPath],
                checkpoint.Directory,
                "restore-patch",
                token);
            if (!apply.Succeeded)
                throw new InvalidOperationException($"Could not restore checkpoint patch: {apply.StandardError}");
        }
        if (newUntrackedDirectories.Count > 0)
        {
            throw new InvalidOperationException(
                "Tracked source was restored, but the attempt created untracked directories that " +
                "require manual review before recursive deletion: " +
                string.Join(", ", newUntrackedDirectories));
        }
    }

    private Dictionary<string, string> CaptureWatchedHashes(
        IEnumerable<string>? additionalPaths = null)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string prefix in _allowedPrefixes.Concat(_managedPaths))
        {
            string absolute = ResolveWorkspaceFile(prefix);
            if (File.Exists(absolute))
            {
                hashes[prefix] = HashFile(absolute);
                continue;
            }
            if (!Directory.Exists(absolute))
                continue;

            foreach (string file in Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories))
            {
                string relative = NormalizeWorkspacePath(file);
                if (IsGeneratedPath(relative))
                    continue;
                hashes[relative] = HashFile(file);
            }
        }
        if (additionalPaths is null)
            return hashes;

        foreach (string path in additionalPaths)
        {
            string normalized = SelfIterationConfiguration.NormalizeRelativePath(path);
            if (hashes.ContainsKey(normalized) || IsGeneratedPath(normalized))
                continue;
            string absolute = ResolveWorkspaceFile(normalized);
            if (File.Exists(absolute))
                hashes[normalized] = HashFile(absolute);
        }
        return hashes;
    }

    private async Task<HashSet<string>> GetUntrackedFilesAsync(
        string evidenceDirectory,
        string stem,
        CancellationToken token)
    {
        SelfIterationProcessResult result = await RunGitAsync(
            ["ls-files", "--others", "--exclude-standard", "-z", "--", "."],
            evidenceDirectory,
            stem,
            token);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Could not enumerate untracked files: {result.StandardError}");
        return result.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(SelfIterationConfiguration.NormalizeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void BackupUntrackedFiles(
        IEnumerable<string> untrackedFiles,
        string checkpointDirectory)
    {
        string backupRoot = Path.Combine(checkpointDirectory, "untracked");
        foreach (string relativePath in untrackedFiles)
        {
            string source = ResolveWorkspaceFile(relativePath);
            if (!File.Exists(source))
                continue;
            string destination = Path.Combine(
                backupRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
    }

    private void RestoreUntrackedFiles(SelfIterationWorkspaceCheckpoint checkpoint)
    {
        string backupRoot = Path.Combine(checkpoint.Directory, "untracked");
        foreach (string relativePath in checkpoint.BackedUpUntrackedFiles)
        {
            string source = Path.Combine(
                backupRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source))
                continue;
            string destination = ResolveWorkspaceFile(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
    }

    private async Task<HashSet<string>> GetGitPathSetAsync(
        IReadOnlyList<string> arguments,
        string evidenceDirectory,
        string stem,
        CancellationToken token)
    {
        SelfIterationProcessResult result = await RunGitAsync(
            arguments,
            evidenceDirectory,
            stem,
            token);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Git path query failed: {result.StandardError}");
        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(SelfIterationConfiguration.NormalizeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task EnsureHeadUnchangedAsync(
        SelfIterationWorkspaceCheckpoint checkpoint,
        string stem,
        CancellationToken token)
    {
        string currentCommit = await ReadGitValueAsync(
            ["rev-parse", "--verify", "HEAD"],
            checkpoint.Directory,
            $"{stem}-head",
            token);
        string currentReference = await ReadGitValueAsync(
            ["rev-parse", "--abbrev-ref", "HEAD"],
            checkpoint.Directory,
            $"{stem}-head-reference",
            token);
        if (!currentCommit.Equals(checkpoint.HeadCommit, StringComparison.Ordinal) ||
            !currentReference.Equals(checkpoint.HeadReference, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The LLM changed the Git commit or branch. Automatic rollback is intentionally " +
                "disabled for repository-history mutations; restore the original branch manually.");
        }
    }

    private async Task<string> ReadGitValueAsync(
        IReadOnlyList<string> arguments,
        string evidenceDirectory,
        string stem,
        CancellationToken token)
    {
        SelfIterationProcessResult result = await RunGitAsync(
            arguments,
            evidenceDirectory,
            stem,
            token);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
            throw new InvalidOperationException($"Git identity query failed: {result.StandardError}");
        return result.StandardOutput.Trim();
    }

    private async Task<HashSet<string>> GetGitLineSetAsync(
        IReadOnlyList<string> arguments,
        string evidenceDirectory,
        string stem,
        CancellationToken token)
    {
        SelfIterationProcessResult result = await RunGitAsync(
            arguments,
            evidenceDirectory,
            stem,
            token);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Git status query failed: {result.StandardError}");
        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task RunGitInChunksAsync(
        IReadOnlyList<string> prefixArguments,
        IEnumerable<string> paths,
        string evidenceDirectory,
        string stem,
        CancellationToken token)
    {
        string[] pathArray = paths.ToArray();
        for (int offset = 0; offset < pathArray.Length; offset += 100)
        {
            List<string> arguments = [.. prefixArguments];
            arguments.AddRange(pathArray.Skip(offset).Take(100));
            SelfIterationProcessResult result = await RunGitAsync(
                arguments,
                evidenceDirectory,
                $"{stem}-{offset / 100 + 1}",
                token);
            if (!result.Succeeded)
                throw new InvalidOperationException($"Git restore failed: {result.StandardError}");
        }
    }

    private Task<SelfIterationProcessResult> RunGitAsync(
        IReadOnlyList<string> arguments,
        string evidenceDirectory,
        string stem,
        CancellationToken token)
        => _processRunner.RunAsync(
            "git",
            arguments,
            _workspaceRoot,
            TimeSpan.FromMinutes(2),
            evidenceDirectory,
            $"{stem}-{Interlocked.Increment(ref _gitInvocation)}",
            cancellationToken: token);

    private string ResolveWorkspaceFile(string relativePath)
    {
        string absolute = Path.GetFullPath(Path.Combine(
            _workspaceRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.GetFullPath(_workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path escapes workspace: {relativePath}");
        return absolute;
    }

    private string NormalizeWorkspacePath(string absolutePath)
        => SelfIterationConfiguration.NormalizeRelativePath(
            Path.GetRelativePath(_workspaceRoot, absolutePath));

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsGeneratedPath(string path)
    {
        string normalized = "/" + path.Replace('\\', '/').Trim('/') + "/";
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/Build/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ParsePorcelainPath(string entry)
    {
        if (entry.Length <= 3)
            return string.Empty;
        string path = entry[3..].Trim();
        int renameSeparator = path.IndexOf(" -> ", StringComparison.Ordinal);
        if (renameSeparator >= 0)
            path = path[(renameSeparator + 4)..];
        return SelfIterationConfiguration.NormalizeRelativePath(path.Trim('"'));
    }
}
