using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XREngine.Assets.Recovery;

/// <summary>
/// Owns the project-local recovery journal for native asset creation. Each operation keeps a
/// manifest, immutable original bytes, working copies, and an append-only journal under
/// <c>&lt;project-root&gt;/.xr-native-asset-recovery/v1/&lt;operation-id&gt;/</c>. Existing asset and
/// metadata formats do not change.
/// </summary>
/// <remarks>
/// <para>
/// The store never overwrites a destination and never deletes a file as compensation. A failure
/// after the first move records <see cref="EAssetCreationRecoveryState.RecoveryRequired"/> and keeps
/// every file. Opening a store reads all records and writes nothing. A pending, partial, or
/// malformed record becomes <see cref="EAssetCreationRecoveryState.RecoveryRequired"/> in memory and
/// protects its public paths.
/// </para>
/// <para>
/// A cooperating-writer lock file serializes journal and quota changes between processes. It does
/// not control other writers. Full power-loss durability is not claimed.
/// </para>
/// </remarks>
public sealed class AssetCreationRecoveryStore
{
    /// <summary>The schema version that this store reads and writes.</summary>
    public const int SchemaVersion = 1;
    /// <summary>The largest number of bytes that the store keeps, including pending reservations.</summary>
    public const long MaximumBytes = 512L * 1024 * 1024;
    /// <summary>The largest number of files that the store keeps, including pending reservations.</summary>
    public const int MaximumFiles = 1024;
    /// <summary>The store directory name under the project root.</summary>
    public const string RootDirectoryName = ".xr-native-asset-recovery";
    internal const string ManifestFileName = "manifest.json";
    internal const string JournalFileName = "journal.jsonl";
    private const string LockFileName = ".lock";
    private const int JournalReserveBytes = 4096;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly Lock _gate = new();
    private readonly List<AssetCreationRecoveryOperation> _operations = [];
    private readonly long _maximumBytes;
    private readonly int _maximumFiles;
    private long _usedBytes;
    private int _usedFiles;

    private AssetCreationRecoveryStore(string projectRoot, string root, long maximumBytes, int maximumFiles)
    {
        ProjectRoot = projectRoot;
        Root = root;
        _maximumBytes = maximumBytes;
        _maximumFiles = maximumFiles;
    }

    /// <summary>Gets the full project root path.</summary>
    public string ProjectRoot { get; }

    /// <summary>Gets the full path of the versioned store directory.</summary>
    public string Root { get; }

    /// <summary>Gets the bytes that the store holds, as counted at open and after each change.</summary>
    public long UsedBytes { get { lock (_gate) return _usedBytes; } }

    /// <summary>Gets the files that the store holds, as counted at open and after each change.</summary>
    public int UsedFiles { get { lock (_gate) return _usedFiles; } }

    /// <summary>Returns a snapshot of all known operations.</summary>
    public AssetCreationRecoveryOperation[] GetOperations()
    {
        lock (_gate)
            return [.. _operations];
    }

    /// <summary>
    /// Opens the store for a project and reads all records. This method writes nothing. Call it
    /// before project watchers or metadata synchronization start.
    /// </summary>
    public static AssetCreationRecoveryStore Open(string projectRoot)
        => Open(projectRoot, MaximumBytes, MaximumFiles);

    /// <summary>Opens the store with explicit limits. Tests use small limits.</summary>
    internal static AssetCreationRecoveryStore Open(string projectRoot, long maximumBytes, int maximumFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        string root = Path.Combine(project, RootDirectoryName, "v" + SchemaVersion);
        var store = new AssetCreationRecoveryStore(project, root, maximumBytes, maximumFiles);
        store.Scan();
        return store;
    }

    /// <summary>
    /// Returns true when an operation protects <paramref name="fullPath"/>, or a file under it,
    /// from automatic asset work.
    /// </summary>
    public bool IsProtected(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return false;
        lock (_gate)
        {
            foreach (AssetCreationRecoveryOperation operation in _operations)
                if (operation.ProtectsPublicPaths && Covers(operation.PublicPaths, fullPath))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// Releases every published operation that covers <paramref name="fullPath"/> or a file under
    /// it, so a normal save, rename, move, or delete can continue. Returns the number released.
    /// </summary>
    public int ReleasePublishedFor(string fullPath, string reason)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return 0;
        List<AssetCreationRecoveryOperation>? found = null;
        lock (_gate)
        {
            foreach (AssetCreationRecoveryOperation operation in _operations)
                if (operation.State == EAssetCreationRecoveryState.Published && Covers(operation.PublicPaths, fullPath))
                    (found ??= []).Add(operation);
        }
        if (found is null)
            return 0;
        foreach (AssetCreationRecoveryOperation operation in found)
            Release(operation, reason);
        return found.Count;
    }

    /// <summary>
    /// Writes the manifest, immutable originals, and working copies for a new creation. The public
    /// paths are not touched. Throws when a public path exists, a path is outside the project or
    /// linked, or the quota does not have space.
    /// </summary>
    public AssetCreationRecoveryOperation Prepare(Guid assetGuid, IReadOnlyList<AssetCreationRecoveryFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
            throw new ArgumentException("A creation needs at least one file.", nameof(files));
        string[] publicPaths = new string[files.Count];
        var entries = new AssetCreationRecoveryEntry[files.Count];
        long reserveBytes = JournalReserveBytes;
        for (int index = 0; index < files.Count; index++)
        {
            string full = Path.GetFullPath(files[index].PublicPath);
            for (int other = 0; other < index; other++)
                if (string.Equals(publicPaths[other], full, PathComparison))
                    throw new ArgumentException($"The creation lists '{full}' twice.", nameof(files));
            RequireProjectPath(full);
            byte[] bytes = files[index].Bytes ?? throw new ArgumentException("A file has no bytes.", nameof(files));
            publicPaths[index] = full;
            entries[index] = new AssetCreationRecoveryEntry(ToRelative(full), bytes.LongLength, Hash(bytes));
            reserveBytes += 2 * bytes.LongLength;
        }
        int reserveFiles = 2 * files.Count + 2;

        using FileStream fileLock = AcquireLock();
        lock (_gate)
        {
            if (_usedBytes + reserveBytes > _maximumBytes || _usedFiles + reserveFiles > _maximumFiles)
                throw new IOException($"AssetRecovery.QuotaExceeded: The recovery store needs {reserveBytes} bytes and {reserveFiles} files. " +
                    $"It holds {_usedBytes} of {_maximumBytes} bytes and {_usedFiles} of {_maximumFiles} files.");
            foreach (string path in publicPaths)
            {
                if (File.Exists(path) || Directory.Exists(path))
                    throw new IOException($"AssetRecovery.PathExists: '{path}' already exists.");
                foreach (AssetCreationRecoveryOperation existing in _operations)
                    if (existing.State != EAssetCreationRecoveryState.Released && ContainsPath(existing.PublicPaths, path))
                        throw new IOException($"AssetRecovery.PathOwned: Operation {existing.Id} owns '{path}'.");
            }

            string id = Guid.NewGuid().ToString("N");
            string directory = Path.Combine(Root, id);
            var manifest = new AssetCreationRecoveryManifest(SchemaVersion, id, assetGuid, entries);
            var operation = new AssetCreationRecoveryOperation(this, id, directory, manifest, publicPaths);
            Directory.CreateDirectory(directory);
            RequireUnlinked(directory);
            _operations.Add(operation);
            _usedBytes += reserveBytes;
            _usedFiles += reserveFiles;
            try
            {
                Append(operation, EAssetCreationRecoveryState.Preparing, null);
                try
                {
                    for (int index = 0; index < files.Count; index++)
                    {
                        WriteNew(operation.OriginalPath(index), files[index].Bytes);
                        WriteNew(operation.WorkingPath(index), files[index].Bytes);
                    }
                    WriteNew(operation.ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest,
                        AssetCreationRecoveryJsonContext.Default.AssetCreationRecoveryManifest));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Fail(operation, "Staging failed: " + exception.Message);
                }
                for (int index = 0; index < entries.Length; index++)
                    if (!Matches(operation.OriginalPath(index), entries[index]) || !Matches(operation.WorkingPath(index), entries[index]))
                        Fail(operation, $"The staged copy {index} does not match its recorded bytes.");
                Append(operation, EAssetCreationRecoveryState.Prepared, null);
                return operation;
            }
            finally { Recount(); }
        }
    }

    /// <summary>
    /// Moves the working copies to their public paths without overwriting. Valid from
    /// <see cref="EAssetCreationRecoveryState.Prepared"/> and <see cref="EAssetCreationRecoveryState.Retained"/>.
    /// </summary>
    public void Publish(AssetCreationRecoveryOperation operation)
    {
        using FileStream fileLock = AcquireLock();
        lock (_gate)
        {
            RequireOwned(operation);
            if (operation.State is not (EAssetCreationRecoveryState.Prepared or EAssetCreationRecoveryState.Retained))
                throw new InvalidOperationException($"AssetRecovery.InvalidState: Operation {operation.Id} is {operation.State} and cannot publish.");
            AssetCreationRecoveryEntry[] entries = operation.Manifest!.Entries;
            for (int index = 0; index < entries.Length; index++)
            {
                string target = operation.PublicPaths[index];
                RequireProjectPath(target);
                if (File.Exists(target) || Directory.Exists(target))
                    throw new IOException($"AssetRecovery.PathExists: '{target}' exists. Nothing was moved.");
                if (!Matches(operation.WorkingPath(index), entries[index]))
                    throw new IOException($"AssetRecovery.WorkingChanged: The working copy for '{target}' changed. Nothing was moved.");
            }
            Append(operation, EAssetCreationRecoveryState.PublishPending, null);
            try
            {
                for (int index = 0; index < entries.Length; index++)
                {
                    string source = operation.WorkingPath(index);
                    string target = operation.PublicPaths[index];
                    try
                    {
                        string? parent = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(parent))
                            Directory.CreateDirectory(parent);
                        RequireProjectPath(target);
                        File.Move(source, target, overwrite: false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        Fail(operation, $"Publishing '{target}' failed: {exception.Message}");
                    }
                    if (!Matches(target, entries[index]))
                        Fail(operation, $"'{target}' does not match its recorded bytes after the move.");
                }
                Append(operation, EAssetCreationRecoveryState.Published, null);
            }
            finally { Recount(); }
        }
    }

    /// <summary>
    /// Moves the public files back to the working copies without overwriting. Valid from
    /// <see cref="EAssetCreationRecoveryState.Published"/>. A changed public file before any move
    /// releases the receipt, keeps the public files, and throws.
    /// </summary>
    public void Retain(AssetCreationRecoveryOperation operation)
    {
        using FileStream fileLock = AcquireLock();
        lock (_gate)
        {
            RequireOwned(operation);
            if (operation.State != EAssetCreationRecoveryState.Published)
                throw new InvalidOperationException($"AssetRecovery.InvalidState: Operation {operation.Id} is {operation.State} and cannot retain.");
            AssetCreationRecoveryEntry[] entries = operation.Manifest!.Entries;
            for (int index = 0; index < entries.Length; index++)
            {
                string target = operation.PublicPaths[index];
                if (!Matches(target, entries[index]))
                {
                    Append(operation, EAssetCreationRecoveryState.Released, $"Foreign change at '{target}'.");
                    throw new IOException($"AssetRecovery.ForeignChange: '{target}' changed after creation. The files stay in place and the receipt is released.");
                }
                if (File.Exists(operation.WorkingPath(index)))
                    Fail(operation, $"The working copy slot for '{target}' is occupied.");
            }
            Append(operation, EAssetCreationRecoveryState.RetainPending, null);
            try
            {
                for (int index = 0; index < entries.Length; index++)
                {
                    string source = operation.PublicPaths[index];
                    string target = operation.WorkingPath(index);
                    try { File.Move(source, target, overwrite: false); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        Fail(operation, $"Retaining '{source}' failed: {exception.Message}");
                    }
                    if (!Matches(target, entries[index]))
                        Fail(operation, $"The retained copy of '{source}' does not match its recorded bytes.");
                }
                Append(operation, EAssetCreationRecoveryState.Retained, null);
            }
            finally { Recount(); }
        }
    }

    /// <summary>
    /// Invalidates the Undo receipt. The files stay where they are. Valid from
    /// <see cref="EAssetCreationRecoveryState.Prepared"/>, <see cref="EAssetCreationRecoveryState.Published"/>,
    /// and <see cref="EAssetCreationRecoveryState.Retained"/>. Other states do nothing.
    /// </summary>
    public void Release(AssetCreationRecoveryOperation operation, string? reason = null)
    {
        using FileStream fileLock = AcquireLock();
        lock (_gate)
        {
            RequireOwned(operation);
            if (operation.State is EAssetCreationRecoveryState.Prepared or EAssetCreationRecoveryState.Published
                or EAssetCreationRecoveryState.Retained)
                Append(operation, EAssetCreationRecoveryState.Released, reason);
        }
    }

    /// <summary>
    /// Records an explicit decision about a recovery-required operation. The files stay where they
    /// are. Afterward, explicit cleanup can remove the operation.
    /// </summary>
    public void AcceptRecovery(AssetCreationRecoveryOperation operation, string decision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decision);
        using FileStream fileLock = AcquireLock();
        lock (_gate)
        {
            RequireOwned(operation);
            if (operation.State != EAssetCreationRecoveryState.RecoveryRequired)
                throw new InvalidOperationException($"AssetRecovery.InvalidState: Operation {operation.Id} is {operation.State}.");
            if (operation.Manifest is null)
                throw new InvalidOperationException($"AssetRecovery.Malformed: Operation {operation.Id} has no readable manifest. Inspect '{operation.Directory}' by hand.");
            Append(operation, EAssetCreationRecoveryState.Released, "Recovery accepted: " + decision);
            operation.Problem = null;
        }
    }

    /// <summary>
    /// Removes a released operation directory after an ownership check. Returns false and keeps the
    /// directory when it holds unknown entries.
    /// </summary>
    public bool RemoveReleased(AssetCreationRecoveryOperation operation)
    {
        using FileStream fileLock = AcquireLock();
        lock (_gate)
        {
            RequireOwned(operation);
            if (operation.State != EAssetCreationRecoveryState.Released || operation.Manifest is null)
                return false;
            int count = operation.Manifest.Entries.Length;
            var known = new HashSet<string>(StringComparer.Ordinal) { ManifestFileName, JournalFileName };
            for (int index = 0; index < count; index++)
            {
                known.Add(Path.GetFileName(operation.OriginalPath(index)));
                known.Add(Path.GetFileName(operation.WorkingPath(index)));
            }
            RequireUnlinked(operation.Directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(operation.Directory))
                if (!known.Contains(Path.GetFileName(entry)) || Directory.Exists(entry))
                    return false;
            Directory.Delete(operation.Directory, recursive: true);
            _operations.Remove(operation);
            Recount();
            return true;
        }
    }

    private void Scan()
    {
        lock (_gate)
        {
            _operations.Clear();
            if (Directory.Exists(Root))
            {
                RequireUnlinked(Root);
                foreach (string directory in Directory.EnumerateDirectories(Root))
                    _operations.Add(ReadOperation(directory));
            }
            Recount();
        }
    }

    private AssetCreationRecoveryOperation ReadOperation(string directory)
    {
        string id = Path.GetFileName(directory);
        AssetCreationRecoveryManifest? manifest = null;
        string? problem = null;
        try
        {
            string manifestPath = Path.Combine(directory, ManifestFileName);
            if (File.Exists(manifestPath))
                manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath),
                    AssetCreationRecoveryJsonContext.Default.AssetCreationRecoveryManifest);
            if (manifest is not null && (manifest.Schema != SchemaVersion || manifest.OperationId != id ||
                manifest.Entries is null || manifest.Entries.Length == 0))
            {
                problem = "The manifest does not match this schema or directory.";
                manifest = null;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            problem = "The manifest is unreadable: " + exception.Message;
            manifest = null;
        }

        string[] publicPaths = [];
        if (manifest is not null)
        {
            publicPaths = new string[manifest.Entries.Length];
            for (int index = 0; index < publicPaths.Length; index++)
            {
                if (!TryFromRelative(manifest.Entries[index].RelativePath, out publicPaths[index]))
                {
                    problem = "The manifest records a path outside the project.";
                    manifest = null;
                    publicPaths = [];
                    break;
                }
            }
        }

        var operation = new AssetCreationRecoveryOperation(this, id, directory, manifest, publicPaths);
        EAssetCreationRecoveryState state = EAssetCreationRecoveryState.Preparing;
        long sequence = 0;
        try
        {
            string journalPath = operation.JournalPath;
            if (!File.Exists(journalPath))
                problem ??= "The journal is missing.";
            else
                foreach (string line in File.ReadLines(journalPath, Encoding.UTF8))
                {
                    if (line.Length == 0)
                        continue;
                    AssetCreationRecoveryRecord? record = JsonSerializer.Deserialize(line,
                        AssetCreationRecoveryJsonContext.Default.AssetCreationRecoveryRecord);
                    if (record is null || record.Sequence != sequence + 1)
                    {
                        problem ??= "The journal sequence is broken.";
                        break;
                    }
                    sequence = record.Sequence;
                    state = record.State;
                }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            problem ??= "The journal is unreadable: " + exception.Message;
        }

        operation.Sequence = sequence;
        if (problem is null && manifest is null)
            problem = "The manifest is missing.";
        if (problem is null)
            problem = state switch
            {
                EAssetCreationRecoveryState.Preparing or EAssetCreationRecoveryState.PublishPending or
                EAssetCreationRecoveryState.RetainPending or EAssetCreationRecoveryState.RestorePending =>
                    $"The operation stopped in {state}.",
                EAssetCreationRecoveryState.Prepared => CheckLocations(operation, working: true, published: false),
                EAssetCreationRecoveryState.Published => CheckLocations(operation, working: false, published: true),
                EAssetCreationRecoveryState.Retained => CheckLocations(operation, working: true, published: false),
                _ => null,
            };
        if (state == EAssetCreationRecoveryState.RecoveryRequired)
            problem ??= "The journal records a required recovery.";
        operation.State = problem is null ? state : EAssetCreationRecoveryState.RecoveryRequired;
        operation.Problem = problem;
        return operation;
    }

    private static string? CheckLocations(AssetCreationRecoveryOperation operation, bool working, bool published)
    {
        AssetCreationRecoveryEntry[] entries = operation.Manifest!.Entries;
        for (int index = 0; index < entries.Length; index++)
        {
            if (!Matches(operation.OriginalPath(index), entries[index]))
                return $"The immutable copy {index} is missing or changed.";
            if (working != Matches(operation.WorkingPath(index), entries[index]))
                return $"The working copy {index} is not where the journal records it.";
            if (published && !Matches(operation.PublicPaths[index], entries[index]))
                return $"'{operation.PublicPaths[index]}' is missing or changed.";
        }
        return null;
    }

    private void Fail(AssetCreationRecoveryOperation operation, string problem)
    {
        operation.Problem = problem;
        try { Append(operation, EAssetCreationRecoveryState.RecoveryRequired, problem); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            operation.State = EAssetCreationRecoveryState.RecoveryRequired;
        }
        throw new IOException($"AssetRecovery.RecoveryRequired: Operation {operation.Id}: {problem} " +
            $"All files are kept. Inspect '{operation.Directory}'.");
    }

    private static void Append(AssetCreationRecoveryOperation operation, EAssetCreationRecoveryState state, string? note)
    {
        var record = new AssetCreationRecoveryRecord(operation.Sequence + 1, state, DateTime.UtcNow, note);
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(record,
            AssetCreationRecoveryJsonContext.Default.AssetCreationRecoveryRecord);
        using (var stream = new FileStream(operation.JournalPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            stream.Write(line);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
        }
        operation.Sequence = record.Sequence;
        operation.State = state;
    }

    private FileStream AcquireLock()
    {
        Directory.CreateDirectory(Root);
        RequireUnlinked(Root);
        string path = Path.Combine(Root, LockFileName);
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 50) { Thread.Sleep(20); }
        }
    }

    private void RequireOwned(AssetCreationRecoveryOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!ReferenceEquals(operation.Store, this) || !_operations.Contains(operation))
            throw new InvalidOperationException("AssetRecovery.ForeignOperation: The operation does not belong to this store.");
        if (operation.Manifest is null)
            throw new InvalidOperationException($"AssetRecovery.Malformed: Operation {operation.Id} has no readable manifest.");
    }

    private void Recount()
    {
        long bytes = 0;
        int count = 0;
        if (Directory.Exists(Root))
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(file).Length; }
                catch (IOException) { }
                count++;
            }
        _usedBytes = bytes;
        _usedFiles = count;
    }

    private void RequireProjectPath(string fullPath)
    {
        string relative = Path.GetRelativePath(ProjectRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(RootDirectoryName, PathComparison))
            throw new ArgumentException($"AssetRecovery.OutsideProject: '{fullPath}' is not a project path.");
        string? current = Path.GetDirectoryName(fullPath);
        while (current is not null && current.Length > ProjectRoot.Length)
        {
            if (Directory.Exists(current))
                RequireUnlinked(current);
            current = Path.GetDirectoryName(current);
        }
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            RequireUnlinked(fullPath);
    }

    private static void RequireUnlinked(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists ? info.LinkTarget is not null : new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException($"AssetRecovery.LinkedPath: '{path}' is a link.");
    }

    private string ToRelative(string fullPath)
        => Path.GetRelativePath(ProjectRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private bool TryFromRelative(string relative, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            return false;
        string candidate = Path.GetFullPath(Path.Combine(ProjectRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        string check = Path.GetRelativePath(ProjectRoot, candidate);
        if (check == ".." || check.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(check))
            return false;
        fullPath = candidate;
        return true;
    }

    private static bool ContainsPath(IReadOnlyList<string> paths, string fullPath)
        => Covers(paths, fullPath, includeDescendants: false);

    private static bool Covers(IReadOnlyList<string> paths, string fullPath, bool includeDescendants = true)
    {
        string candidate;
        try { candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
        foreach (string path in paths)
        {
            if (string.Equals(path, candidate, PathComparison))
                return true;
            if (includeDescendants && path.Length > candidate.Length + 1 &&
                path.StartsWith(candidate, PathComparison) &&
                (path[candidate.Length] == Path.DirectorySeparatorChar || path[candidate.Length] == Path.AltDirectorySeparatorChar))
                return true;
        }
        return false;
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static bool Matches(string path, AssetCreationRecoveryEntry entry)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != entry.Length || info.LinkTarget is not null)
                return false;
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), entry.Sha256, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
