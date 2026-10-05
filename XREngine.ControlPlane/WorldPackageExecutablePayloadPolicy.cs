using System.Buffers.Binary;

namespace XREngine.ControlPlane;

/// <summary>
/// Classifies package files that would carry executable code. Downloaded worlds and
/// avatars are data only; see the linked policy document for the rationale and the
/// boundaries that enforce it.
/// </summary>
public static class WorldPackageExecutablePayloadPolicy
{
    /// <summary>Repository-relative path of the policy that every diagnostic names.</summary>
    public const string PolicyDocumentPath = "docs/architecture/runtime/downloadable-content-execution-policy.md";

    /// <summary>Number of leading bytes required to classify every supported executable signature.</summary>
    public const int SignatureProbeBytes = 4;

    private static readonly string[] ForbiddenExtensions =
    [
        ".dll", ".exe", ".so", ".dylib", ".cs", ".csproj", ".ps1", ".bat", ".cmd", ".sh",
    ];

    /// <summary>Returns true when the relative path carries an extension that is never allowed in a package.</summary>
    public static bool TryClassifyByExtension(string relativePath, out string kind)
    {
        kind = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        string extension = Path.GetExtension(relativePath);
        if (string.IsNullOrEmpty(extension))
            return false;

        for (int i = 0; i < ForbiddenExtensions.Length; i++)
        {
            if (string.Equals(extension, ForbiddenExtensions[i], StringComparison.OrdinalIgnoreCase))
            {
                kind = $"forbidden extension '{extension}'";
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns true when the leading bytes match a PE, ELF, or Mach-O image signature.</summary>
    public static bool TryClassifyBySignature(ReadOnlySpan<byte> leadingBytes, out string kind)
    {
        kind = string.Empty;
        if (leadingBytes.Length < 2)
            return false;

        // PE images start with the MS-DOS stub signature. The CLI header is irrelevant here:
        // native and managed images are both executable code.
        if (leadingBytes[0] == (byte)'M' && leadingBytes[1] == (byte)'Z')
        {
            kind = "PE image";
            return true;
        }

        if (leadingBytes.Length < 4)
            return false;

        if (leadingBytes[0] == 0x7F && leadingBytes[1] == (byte)'E' && leadingBytes[2] == (byte)'L' && leadingBytes[3] == (byte)'F')
        {
            kind = "ELF image";
            return true;
        }

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(leadingBytes);
        switch (magic)
        {
            case 0xFEEDFACE:
            case 0xFEEDFACF:
            case 0xCEFAEDFE:
            case 0xCFFAEDFE:
                kind = "Mach-O image";
                return true;
            case 0xCAFEBABE:
            case 0xBEBAFECA:
            case 0xCAFEBABF:
            case 0xBFBAFECA:
                kind = "Mach-O fat image";
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Classifies a file on disk by extension and then by content signature. Returns false for
    /// ordinary data files. Unreadable files fail closed; callers report missing files separately.
    /// </summary>
    public static bool TryClassifyFile(string relativePath, string fullPath, out string kind)
    {
        if (TryClassifyByExtension(relativePath, out kind))
            return true;

        Span<byte> probe = stackalloc byte[SignatureProbeBytes];
        int read;
        try
        {
            using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            read = stream.Read(probe);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Cannot verify data-only package file '{relativePath}'; inspection failed. See {PolicyDocumentPath}.", error);
        }

        return TryClassifyBySignature(probe[..read], out kind);
    }

    /// <summary>Builds the diagnostic text used by every enforcement boundary.</summary>
    public static string DescribeRejection(string relativePath, string kind)
        => $"World package file '{relativePath}' is executable content ({kind}). Downloaded worlds and avatars carry data only; see {PolicyDocumentPath}.";
}
