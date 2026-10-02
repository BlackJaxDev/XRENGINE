using System.Security.Cryptography;

/// <summary>Copies only authored project inputs and detects changes to either copy after publishing.</summary>
internal sealed class CanonicalProjectSnapshot
{
    private readonly string _sourceRoot;
    private readonly string _copyRoot;
    private readonly IReadOnlyDictionary<string, string> _sourceHashes;
    private readonly bool _includeMetadata;

    private CanonicalProjectSnapshot(string sourceRoot, string copyRoot, string projectName,
        IReadOnlyDictionary<string, string> sourceHashes, bool includeMetadata)
    {
        _sourceRoot = sourceRoot;
        _copyRoot = copyRoot;
        ProjectFile = Path.Combine(copyRoot, projectName);
        _sourceHashes = sourceHashes;
        _includeMetadata = includeMetadata;
    }

    public string ProjectFile { get; }

    public static CanonicalProjectSnapshot Copy(string projectFile, string copyRoot)
    {
        string sourceRoot = Path.GetDirectoryName(projectFile)!;
        if (Directory.Exists(copyRoot) || File.Exists(copyRoot))
            throw new IOException("The validation project directory must be new and owned by this run.");
        string metadataPath = Path.Combine(sourceRoot, "Metadata");
        if (File.Exists(metadataPath))
            throw new InvalidDataException("Canonical project Metadata must be a directory when present.");
        bool includeMetadata = Directory.Exists(metadataPath);
        Dictionary<string, string> hashes = ReadHashes(sourceRoot, Path.GetFileName(projectFile), includeMetadata);
        Directory.CreateDirectory(copyRoot);
        foreach (string name in includeMetadata ? new[] { "Assets", "Config", "Metadata" } : new[] { "Assets", "Config" })
            Directory.CreateDirectory(Path.Combine(copyRoot, name));
        foreach (string relative in hashes.Keys)
        {
            string destination = Path.Combine(copyRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(sourceRoot, relative), destination);
        }
        CanonicalProjectSnapshot snapshot = new(sourceRoot, copyRoot, Path.GetFileName(projectFile), hashes, includeMetadata);
        snapshot.VerifyUnchanged();
        Console.WriteLine($"CANONICAL_INPUTS_COPIED files={hashes.Count}");
        return snapshot;
    }

    public void VerifyUnchanged()
    {
        Verify(_sourceRoot, "canonical source");
        Verify(_copyRoot, "validation copy");
        Console.WriteLine($"CANONICAL_INPUTS_UNCHANGED files={_sourceHashes.Count}");
    }

    private void Verify(string root, string label)
    {
        Dictionary<string, string> actual = ReadHashes(root, Path.GetFileName(ProjectFile), _includeMetadata);
        if (actual.Count != _sourceHashes.Count || _sourceHashes.Any(entry =>
            !actual.TryGetValue(entry.Key, out string? hash) || hash != entry.Value))
            throw new InvalidDataException($"{label} authored input bytes changed during browser publishing.");
    }

    private static Dictionary<string, string> ReadHashes(string root, string projectName, bool includeMetadata)
    {
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        AddFile(projectName);
        foreach (string name in includeMetadata ? new[] { "Assets", "Config", "Metadata" } : new[] { "Assets", "Config" })
        {
            string directory = Path.Combine(root, name);
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Canonical project is missing {name}.");
            AddDirectory(directory);
        }
        return hashes;

        void AddDirectory(string directory)
        {
            DirectoryInfo info = new(directory);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Canonical project inputs cannot contain directory links.");
            foreach (string file in Directory.EnumerateFiles(directory))
                AddFile(Path.GetRelativePath(root, file));
            foreach (string child in Directory.EnumerateDirectories(directory))
                AddDirectory(child);
        }

        void AddFile(string relative)
        {
            string file = Path.Combine(root, relative);
            FileInfo info = new(file);
            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Canonical project inputs must be regular files.");
            hashes.Add(relative, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        }
    }
}
