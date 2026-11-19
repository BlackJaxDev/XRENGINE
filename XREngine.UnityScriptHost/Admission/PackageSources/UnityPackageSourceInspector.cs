using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XREngine.UnityScriptHost.Admission.PackageSources;

/// <summary>Reads a local package manifest and fingerprints its complete source tree.</summary>
public sealed class UnityPackageSourceInspector
{
    private const int MaxEntries = 100_000;
    private const int MaxDepth = 64;
    private const long MaxFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxTotalBytes = 32L * 1024 * 1024 * 1024;
    private const int MaxManifestBytes = 4 * 1024 * 1024;

    /// <summary>Returns a path-independent digest after it hashes every file in one package source directory.</summary>
    public UnityPackageSourceInventory Inspect(string packageDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));
        UnitySourceInventoryBuilder.EnsureNoReparsePoints(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The pinned package source directory does not exist.");

        string manifest = Path.Combine(root, "package.json");
        UnitySourceInventoryBuilder.EnsureNoReparsePoints(manifest);
        byte[] manifestBytes = ReadManifest(manifest);
        bool hasBom = manifestBytes.Length >= 3 && manifestBytes[0] == 0xEF &&
            manifestBytes[1] == 0xBB && manifestBytes[2] == 0xBF;
        using JsonDocument document = JsonDocument.Parse(hasBom
            ? manifestBytes.AsMemory(3) : manifestBytes.AsMemory(), new JsonDocumentOptions { MaxDepth = 64 });
        JsonElement metadata = document.RootElement;
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A package manifest must be a JSON object.");
        string name = ReadRequiredString(metadata, "name");
        string version = ReadRequiredString(metadata, "version");
        if (!IsValidPackageName(name))
            throw new InvalidDataException("A package name must be a bounded lowercase Unity package identifier.");

        var entries = new List<UnitySourceEntry>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        int discovered = 0;
        Visit(root, string.Empty, 0);
        entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        UnitySourceEntry inspectedManifest = entries.FirstOrDefault(static entry => entry.Path == "package.json")
            ?? throw new InvalidDataException("A package source manifest was not inventoried.");
        if (inspectedManifest.Length != manifestBytes.LongLength ||
            !inspectedManifest.Sha256.Equals(Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The package manifest changed during inspection.");
        string digest = ComputeDigest(name, version, entries);
        return new UnityPackageSourceInventory
        {
            Name = name,
            Version = version,
            ContentSha256 = digest,
            TotalFileBytes = totalBytes,
            Entries = [.. entries],
        };

        void Visit(string directory, string relativeDirectory, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > MaxDepth)
                throw new InvalidDataException("A package source directory exceeds the depth limit.");
            UnitySourceInventoryBuilder.EnsureNoReparsePoints(directory);
            if (relativeDirectory.Length != 0)
                Add(new(relativeDirectory, "Directory", 0, string.Empty));

            var children = new List<string>();
            foreach (string child in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++discovered > MaxEntries)
                    throw new InvalidDataException("The package source entry count exceeds the inspection limit.");
                children.Add(child);
            }
            children.Sort(StringComparer.Ordinal);
            foreach (string child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string childRelative = relativeDirectory.Length == 0
                    ? Path.GetFileName(child) : relativeDirectory + "/" + Path.GetFileName(child);
                if (childRelative.Length > 2048)
                    throw new InvalidDataException("A package source path exceeds the length limit.");
                FileAttributes attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Package source reparse points are not supported.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Visit(child, childRelative, depth + 1);
                    continue;
                }
                using var stream = new FileStream(child, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.SequentialScan);
                if (stream.Length > MaxFileBytes || stream.Length > MaxTotalBytes - totalBytes)
                    throw new InvalidDataException("Package source bytes exceed the inspection limit.");
                long length = stream.Length;
                string hash = HashStream(stream, length, cancellationToken);
                totalBytes += length;
                Add(new(childRelative, Path.GetExtension(child).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                    ? "Script" : "File", length, hash));
            }
        }

        void Add(UnitySourceEntry entry)
        {
            if (entries.Count >= MaxEntries || !paths.Add(entry.Path))
                throw new InvalidDataException("Package source entries exceed the limit or collide under Windows comparison.");
            entries.Add(entry);
        }
    }

    /// <summary>Accepts Unity package names that can also name a safe destination directory.</summary>
    public static bool IsValidPackageName(string name)
        => name is not null && name.Length is > 0 and <= 200 &&
            Regex.IsMatch(name, @"\A[a-z0-9][a-z0-9._-]*\z", RegexOptions.CultureInvariant) &&
            name != "." && name != ".." && !name.EndsWith('.');

    private static byte[] ReadManifest(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxManifestBytes)
            throw new InvalidDataException("A package manifest exceeds the metadata byte limit.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("A package manifest changed while reading.");
        return bytes;
    }

    private static string ReadRequiredString(JsonElement metadata, string property)
    {
        if (!metadata.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not string text || string.IsNullOrWhiteSpace(text) || text.Length > 2048)
            throw new InvalidDataException($"A package manifest needs a bounded '{property}' string.");
        return text;
    }

    internal static string HashStream(Stream stream, long length, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long count = 0;
            int read;
            while ((read = stream.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                count += read;
                if (count > length)
                    throw new IOException("A package source file changed while hashing.");
                hash.AppendData(buffer, 0, read);
            }
            if (count != length)
                throw new IOException("A package source file changed while hashing.");
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string ComputeDigest(string name, string version, List<UnitySourceEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append("unity-package-source-v1");
        Append(name);
        Append(version);
        foreach (UnitySourceEntry entry in entries)
        {
            Append(entry.Path);
            Append(entry.Kind);
            Append(entry.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(entry.Sha256);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
            hash.AppendData(size);
            hash.AppendData(bytes);
        }
    }
}
