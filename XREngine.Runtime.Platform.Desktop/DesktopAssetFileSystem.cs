using SimpleScene.Util.ssBVH;
using XREngine.Core.Files;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Desktop host file discovery through the operating system's ordinary filesystem permissions.</summary>
public sealed class DesktopAssetFileSystem : IAssetFileSystem, IBvhDiskCacheBackend
{
    public bool SupportsChangeNotifications => true;
    public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption searchOption)
        => Directory.EnumerateFiles(path, pattern, searchOption);
    public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption searchOption)
        => Directory.EnumerateDirectories(path, pattern, searchOption);
    public IEnumerable<string> EnumerateFileSystemEntries(string path)
        => Directory.EnumerateFileSystemEntries(path);
    public IAssetChangeMonitor CreateChangeMonitor() => new DesktopAssetChangeMonitor();

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
