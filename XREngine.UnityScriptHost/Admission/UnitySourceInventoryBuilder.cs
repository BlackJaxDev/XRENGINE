using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace XREngine.UnityScriptHost.Admission;

/// <summary>Fingerprints authoring inputs and reads metadata without loading package code.</summary>
public sealed partial class UnitySourceInventoryBuilder
{
    private const int MaxEntries = 100_000;
    private const int MaxDepth = 64;
    private const long MaxFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxTotalBytes = 32L * 1024 * 1024 * 1024;
    private const int MaxMetadataBytes = 4 * 1024 * 1024;
    private const int MaxDiagnostics = 4096;
    private const int MaxMetadataStringLength = 2048;
    private const int MaxMetadataItems = 4096;
    private const long MaxRequiredMetadataBytes = 48 * 1024 * 1024;
    private const long MaxOptionalMetadataBytes = 16 * 1024 * 1024;
    private static readonly string[] SourceTrees = ["Assets", "Packages", "ProjectSettings"];
    private readonly List<UnitySourceEntry> _entries = [];
    private readonly List<UnitySourcePackage> _packages = [];
    private readonly List<UnityAssemblyDefinition> _assemblies = [];
    private readonly List<UnityAssemblyReference> _references = [];
    private readonly List<UnityInventoryDiagnostic> _diagnostics = [];
    private long _totalBytes;
    private long _requiredMetadataBytes;
    private long _optionalMetadataBytes;
    private int _discoveredEntries;
    private int _suppressedDiagnosticCount;
    private string _editorVersion = string.Empty;

    /// <summary>Inspects a project once. Entry selection is fingerprinted but hierarchy selection is not resolved.</summary>
    public UnitySourceInventory Inspect(string projectRoot, string entryPath, string rootName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(entryPath);
        ArgumentNullException.ThrowIfNull(rootName);
        if (rootName.Length > MaxMetadataStringLength)
            throw new ArgumentException("The selected root name exceeds the inspection limit.");
        if (_entries.Count != 0)
            throw new InvalidOperationException("Create a new inventory builder for each inspection.");

        string root = Path.GetFullPath(projectRoot);
        EnsureNoReparsePoints(root);
        if (!Directory.Exists(Path.Combine(root, "Assets")))
            throw new InvalidDataException("The source project must contain an Assets directory.");

        string normalizedEntry = NormalizeEntryPath(entryPath);
        if (normalizedEntry.Length != 0)
        {
            string entry = Path.Combine(root, normalizedEntry.Replace('/', Path.DirectorySeparatorChar));
            EnsureNoReparsePoints(entry);
            if (!File.Exists(entry))
                throw new FileNotFoundException("The selected source entry does not exist.", normalizedEntry);
        }

        foreach (string tree in SourceTrees)
        {
            string path = Path.Combine(root, tree);
            if (Directory.Exists(path))
            {
                DiscoverEntry(root, path);
                ReadDirectory(root, path, 0, cancellationToken);
            }
            else if (File.Exists(path))
                throw new InvalidDataException($"Source tree '{tree}' is a file instead of a directory.");
        }

        _entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (UnitySourceEntry entry in _entries)
            if (!uniquePaths.Add(entry.Path))
                throw new InvalidDataException($"Source paths collide under Windows comparison: '{entry.Path}'.");

        AddDiagnostic(new("UNITYHOST0001", string.Empty,
            "Inspection does not compile or execute scripts. Compatibility APIs, build execution, and native output are not available."));

        return new UnitySourceInventory
        {
            SourceContentSha256 = ComputeDigest(normalizedEntry, rootName),
            EntryPath = normalizedEntry,
            RootName = rootName,
            UnityEditorVersion = _editorVersion,
            TotalFileBytes = _totalBytes,
            SuppressedDiagnosticCount = _suppressedDiagnosticCount,
            Entries = [.. _entries],
            Packages = [.. _packages.OrderBy(static package => package.Path, StringComparer.Ordinal)],
            AssemblyDefinitions = [.. _assemblies.OrderBy(static assembly => assembly.Path, StringComparer.Ordinal)],
            AssemblyReferences = [.. _references.OrderBy(static reference => reference.Path, StringComparer.Ordinal)],
            Diagnostics = [.. _diagnostics.OrderBy(static diagnostic => diagnostic.Path, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)],
        };
    }

    private void ReadDirectory(string root, string directory, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoReparsePoints(directory);
        if (depth > MaxDepth)
            throw new InvalidDataException("The source directory depth exceeds the inspection limit.");
        AddEntry(new(Relative(root, directory), "Directory", 0, string.Empty));

        var children = new List<string>();
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscoverEntry(root, path);
            children.Add(path);
        }
        children.Sort(StringComparer.Ordinal);
        foreach (string path in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Source reparse points are not supported: '{Relative(root, path)}'.");
            if ((attributes & FileAttributes.Directory) != 0)
                ReadDirectory(root, path, depth + 1, cancellationToken);
            else
                ReadFile(root, path, cancellationToken);
        }
    }

    private void ReadFile(string root, string path, CancellationToken cancellationToken)
    {
        EnsureNoReparsePoints(path);
        string relative = Relative(root, path);
        DateTime modified = File.GetLastWriteTimeUtc(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        long length = stream.Length;
        if (length > MaxFileBytes || length > MaxTotalBytes - _totalBytes)
            throw new InvalidDataException($"Source byte limit exceeded at '{relative}'.");
        _totalBytes += length;

        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        string digest;
        byte[] header = new byte[4];
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long readBytes = 0;
            int read;
            while ((read = stream.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (readBytes < header.Length)
                    buffer.AsSpan(0, Math.Min(read, header.Length - (int)readBytes)).CopyTo(header.AsSpan((int)readBytes));
                readBytes += read;
                if (readBytes > length)
                    throw new IOException($"Source length changed during inspection: '{relative}'.");
                hash.AppendData(buffer, 0, read);
            }
            if (readBytes != length)
                throw new IOException($"Source length changed during inspection: '{relative}'.");
            digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        string extension = Path.GetExtension(path).ToLowerInvariant();
        string kind = GetKind(extension, header);
        AddEntry(new(relative, kind, length, digest));
        if (kind == "Executable")
            AddDiagnostic(new("UNITYHOST1001", relative,
                "Package executable data was found. Initial script execution admits recompiled source only; binary compatibility and platform admission are unresolved."));

        bool ignoredAssemblyMetadata = extension is ".asmdef" or ".asmref" && UnitySourcePath.IsIgnoredCompilationPath(relative);
        if (IsMetadata(relative, extension) && !ignoredAssemblyMetadata)
            ReadFileMetadata(stream, relative, extension);

        if (stream.Length != length || File.GetLastWriteTimeUtc(path) != modified)
            throw new IOException($"Source changed during inspection: '{relative}'.");
    }

    private void AddEntry(UnitySourceEntry entry)
    {
        if (entry.Path.Length > MaxMetadataStringLength)
            throw new InvalidDataException("A source path exceeds the inspection limit.");
        if (_entries.Count >= MaxEntries)
            throw new InvalidDataException("The source entry count exceeds the inspection limit.");
        _entries.Add(entry);
    }

    private void DiscoverEntry(string root, string path)
    {
        if (++_discoveredEntries > MaxEntries || Relative(root, path).Length > MaxMetadataStringLength)
            throw new InvalidDataException("The source entry count or relative path length exceeds the inspection limit.");
    }

    private void AddDiagnostic(UnityInventoryDiagnostic diagnostic)
    {
        if (diagnostic.Message.Length > 8192)
            throw new InvalidDataException("An inventory diagnostic exceeds the text limit.");
        if (_diagnostics.Count < MaxDiagnostics)
            _diagnostics.Add(diagnostic);
        else
            _suppressedDiagnosticCount++;
    }

    private string ComputeDigest(string entryPath, string rootName)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestField(hash, "unity-source-inventory-v1");
        AppendDigestField(hash, entryPath);
        AppendDigestField(hash, rootName);
        foreach (UnitySourceEntry entry in _entries)
        {
            AppendDigestField(hash, entry.Path);
            AppendDigestField(hash, entry.Kind);
            AppendDigestField(hash, entry.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendDigestField(hash, entry.Sha256);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendDigestField(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
        hash.AppendData(size);
        hash.AppendData(bytes);
    }

    internal static void EnsureNoReparsePoints(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Source and output paths must not cross reparse points.");
            current = Path.GetDirectoryName(current);
        }
    }

    private static string NormalizeEntryPath(string value)
    {
        if (value.Length == 0)
            return string.Empty;
        string path = value.Replace('\\', '/');
        if (path.Length > MaxMetadataStringLength || !path.StartsWith("Assets/", StringComparison.Ordinal) || path.Contains(':') ||
            path.Split('/').Any(static part => part is "" or "." or "..") ||
            !(path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The entry must be an Assets-relative .unity or .prefab path without traversal.");
        return path;
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string GetKind(string extension, ReadOnlySpan<byte> header)
    {
        uint signature = BinaryPrimitives.ReadUInt32BigEndian(header);
        if ((header[0] == 'M' && header[1] == 'Z') || signature is 0x7F454C46 or 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA or 0xCAFEBABF or 0xBFBAFECA ||
            extension is ".dll" or ".exe" or ".so" or ".dylib")
            return "Executable";
        return extension switch
        {
            ".cs" => "Script",
            ".asmdef" => "AssemblyDefinition",
            ".asmref" => "AssemblyReference",
            ".meta" => "Metadata",
            ".unity" => "Scene",
            ".prefab" => "Prefab",
            _ => "Asset",
        };
    }
}
