namespace XREngine.Core.Files;

/// <summary>
/// One-shot archive reads for tooling and tests. Each call opens a temporary
/// <see cref="PublishedArchiveHandle"/>, so the format definition lives in one place. Runtime
/// loads use <see cref="PublishedArchiveRegistry"/> and keep their handles open.
/// </summary>
public static class AssetArchiveReader
{
    public static byte[] GetAsset(string archiveFilePath, string assetPath)
    {
        using PublishedArchiveHandle handle = PublishedArchiveHandle.Open(archiveFilePath);
        return handle.ReadAssetBytes(assetPath);
    }

    public static IReadOnlyList<string> GetAssetPaths(string archiveFilePath)
    {
        using PublishedArchiveHandle handle = PublishedArchiveHandle.Open(archiveFilePath);
        return handle.GetAssetPaths();
    }
}
