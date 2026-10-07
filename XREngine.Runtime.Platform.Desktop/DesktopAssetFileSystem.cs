using SimpleScene.Util.ssBVH;
using XREngine.Core.Files;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Provides host file discovery and metadata operations under ordinary filesystem permissions.</summary>
public sealed class DesktopAssetFileSystem : IAssetFileSystem, IAssetMetadataFileBackend, IBvhDiskCacheBackend
{
    public bool SupportsChangeNotifications => true;
    public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption searchOption)
        => Directory.EnumerateFiles(path, pattern, searchOption);
    public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption searchOption)
        => Directory.EnumerateDirectories(path, pattern, searchOption);
    public IEnumerable<string> EnumerateFileSystemEntries(string path)
        => Directory.EnumerateFileSystemEntries(path);
    public IAssetChangeMonitor CreateChangeMonitor() => new DesktopAssetChangeMonitor();

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteDirectory(string path) => Directory.Delete(path);
    public void DeleteFile(string path) => File.Delete(path);
    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        => File.Move(sourcePath, destinationPath, overwrite);
    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
        => File.Copy(sourcePath, destinationPath, overwrite);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public Guid TryExtractAssetGuid(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Guid.Empty;

        const int maxAttempts = 3;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line) || char.IsWhiteSpace(line[0]))
                        continue;

                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("ID:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (Guid.TryParse(trimmed[3..].Trim(), out Guid guid))
                        return guid;
                }

                break;
            }
            catch (IOException)
            {
                if (attempt == maxAttempts - 1)
                    break;

                Thread.Sleep(15 * (attempt + 1));
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
        }

        return Guid.Empty;
    }

    bool IBvhDiskCacheBackend.TryLoad(
        string? cacheRoot,
        List<Triangle> triangles,
        List<IndexTriangle> indexTriangles,
        out BVH<Triangle>? bvh,
        out Dictionary<Triangle, (IndexTriangle Indices, int FaceIndex)>? triangleLookup)
        => DesktopBvhDiskCache.TryLoad(cacheRoot, triangles, indexTriangles, out bvh, out triangleLookup);

    void IBvhDiskCacheBackend.TryStore(
        string? cacheRoot,
        List<Triangle> triangles,
        Dictionary<Triangle, (IndexTriangle Indices, int FaceIndex)> triangleLookup,
        BVH<Triangle> bvh)
        => DesktopBvhDiskCache.TryStore(cacheRoot, triangles, triangleLookup, bvh);
}
