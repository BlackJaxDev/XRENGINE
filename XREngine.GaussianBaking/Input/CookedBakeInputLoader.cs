using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using XREngine.ControlPlane;
using XREngine.Core.Files;
using XREngine.Scene;
using XREngine.Scene.Prefabs;

namespace XREngine.Rendering.Models.Gaussian.Baking.Input;

/// <summary>
/// Loads a cooked prefab or world from a <c>.pak</c> archive for a bake. The loader examines
/// every archive entry before it deserializes data. It rejects executable content and entry
/// paths that are rooted or that contain a <c>..</c> segment.
/// </summary>
public static class CookedBakeInputLoader
{
    private const string ReflectionWarningMessage =
        "Cooked bake input loading uses the authoring cooked asset reader, which relies on reflection.";

    /// <summary>
    /// Loads <paramref name="assetPath"/> from <paramref name="pakPath"/>. An avatar or an item
    /// must be an <see cref="XRPrefabSource"/>, which the loader instantiates. A world must be an
    /// <see cref="XRWorld"/> with a reference graph.
    /// </summary>
    /// <exception cref="GaussianBakeException">
    /// <see cref="GaussianBakeFailureCodes.ContentRejected"/> when an entry is not safe;
    /// <see cref="GaussianBakeFailureCodes.InputLoadFailed"/> when the archive or the asset cannot be read or has the wrong type;
    /// <see cref="GaussianBakeFailureCodes.ContentEmpty"/> when the prefab has no root node.
    /// </exception>
    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    public static CookedBakeInput Load(string pakPath, string assetPath, GaussianBakeKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The bake kind is not known.");

        if (!File.Exists(pakPath))
            throw new GaussianBakeException(GaussianBakeFailureCodes.InputLoadFailed, $"The input archive '{pakPath}' does not exist.");

        // Archive entry names use forward slashes.
        string normalizedAssetPath = assetPath.Replace('\\', '/');
        byte[] assetBytes = ReadVerifiedAsset(pakPath, normalizedAssetPath);
        string inputSha256 = ComputeSha256(pakPath);

        if (kind == GaussianBakeKind.World)
        {
            XRWorld world = Deserialize(assetBytes, typeof(XRWorld), requireReferenceGraph: true, pakPath, normalizedAssetPath) as XRWorld
                ?? throw WrongType(pakPath, normalizedAssetPath, typeof(XRWorld));
            return new CookedBakeInput
            {
                World = world,
                Kind = kind,
                InputSha256 = inputSha256,
                AssetPath = normalizedAssetPath,
            };
        }

        XRPrefabSource prefab = Deserialize(assetBytes, typeof(XRPrefabSource), requireReferenceGraph: false, pakPath, normalizedAssetPath) as XRPrefabSource
            ?? throw WrongType(pakPath, normalizedAssetPath, typeof(XRPrefabSource));
        if (prefab.RootNode is null)
            throw new GaussianBakeException(GaussianBakeFailureCodes.ContentEmpty, $"The prefab '{normalizedAssetPath}' in '{pakPath}' has no root node.");

        return new CookedBakeInput
        {
            PrefabRoot = prefab.Instantiate(null, null),
            Kind = kind,
            InputSha256 = inputSha256,
            AssetPath = normalizedAssetPath,
        };
    }

    /// <summary>
    /// Examines the path of every entry, then the leading bytes of every entry, and then returns
    /// a copy of the requested entry. No entry is deserialized here.
    /// </summary>
    private static byte[] ReadVerifiedAsset(string pakPath, string assetPath)
    {
        try
        {
            using PublishedArchiveHandle archive = PublishedArchiveHandle.Open(pakPath);
            IReadOnlyList<string> entries = archive.GetAssetPaths();
            for (int i = 0; i < entries.Count; i++)
                RejectUnsafePath(pakPath, entries[i]);

            for (int i = 0; i < entries.Count; i++)
                RejectExecutableSignature(archive, i, pakPath, entries[i]);

            if (!archive.Contains(assetPath))
                throw new GaussianBakeException(GaussianBakeFailureCodes.InputLoadFailed, $"The input archive '{pakPath}' has no entry '{assetPath}'.");

            return archive.ReadAssetBytes(assetPath);
        }
        catch (Exception error) when (IsLoadFailure(error))
        {
            throw new GaussianBakeException(GaussianBakeFailureCodes.InputLoadFailed, $"Cannot read the input archive '{pakPath}': {error.Message}");
        }
    }

    private static void RejectUnsafePath(string pakPath, string entry)
    {
        if (IsEscapingPath(entry))
        {
            throw new GaussianBakeException(
                GaussianBakeFailureCodes.ContentRejected,
                $"Archive entry '{entry}' in '{pakPath}' is a rooted path or contains a '..' segment.");
        }

        if (WorldPackageExecutablePayloadPolicy.TryClassifyByExtension(entry, out string payloadKind))
            throw ExecutableRejected(pakPath, entry, payloadKind);
    }

    private static void RejectExecutableSignature(PublishedArchiveHandle archive, int entryIndex, string pakPath, string entry)
    {
        using CookedPayloadLease lease = archive.ReadEntry(entryIndex);
        ReadOnlySpan<byte> payload = lease.Span;
        ReadOnlySpan<byte> probe = payload[..Math.Min(payload.Length, WorldPackageExecutablePayloadPolicy.SignatureProbeBytes)];
        if (WorldPackageExecutablePayloadPolicy.TryClassifyBySignature(probe, out string payloadKind))
            throw ExecutableRejected(pakPath, entry, payloadKind);
    }

    private static GaussianBakeException ExecutableRejected(string pakPath, string entry, string payloadKind)
        => new(
            GaussianBakeFailureCodes.ContentRejected,
            $"{WorldPackageExecutablePayloadPolicy.DescribeRejection(entry, payloadKind)} Archive: '{pakPath}'.");

    private static bool IsEscapingPath(string entry)
    {
        if (Path.IsPathRooted(entry) || entry.StartsWith('/') || entry.StartsWith('\\'))
            return true;

        foreach (string segment in entry.Split('/', '\\'))
        {
            if (segment == "..")
                return true;
        }

        return false;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object? Deserialize(byte[] assetBytes, Type expectedType, bool requireReferenceGraph, string pakPath, string assetPath)
    {
        try
        {
            return CookedAssetReader.LoadAsset(assetBytes, expectedType, requireReferenceGraph);
        }
        catch (Exception error) when (IsLoadFailure(error))
        {
            throw new GaussianBakeException(
                GaussianBakeFailureCodes.InputLoadFailed,
                $"Cannot load '{assetPath}' from '{pakPath}' as {expectedType.Name}: {error.Message}");
        }
    }

    private static string ComputeSha256(string pakPath)
    {
        try
        {
            using FileStream stream = new(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, FileOptions.SequentialScan);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new GaussianBakeException(GaussianBakeFailureCodes.InputLoadFailed, $"Cannot hash the input archive '{pakPath}': {error.Message}");
        }
    }

    private static GaussianBakeException WrongType(string pakPath, string assetPath, Type expectedType)
        => new(GaussianBakeFailureCodes.InputLoadFailed, $"The asset '{assetPath}' in '{pakPath}' is not {expectedType.Name}.");

    /// <summary>Returns true for errors that a damaged, wrong, or unreadable input causes.</summary>
    private static bool IsLoadFailure(Exception error)
        => error is IOException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException
            or FormatException
            or KeyNotFoundException
            or IndexOutOfRangeException
            or InvalidCastException
            or UnauthorizedAccessException;
}
