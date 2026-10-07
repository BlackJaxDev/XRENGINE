using System.IO.MemoryMappedFiles;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Browser;

/// <summary>Writes browser MEMFS buffer spills and owns their copy-on-write mappings.</summary>
public sealed unsafe class BrowserBufferSpillStorage : IXRBufferSpillStorage
{
    private string? _directory;

    public IXRBufferSpillLease WriteAndMap(ReadOnlySpan<byte> bytes)
    {
        // A second session can remove an empty directory. Recreate it for each spill.
        string directory = Directory.CreateDirectory(ResolveDirectory()).FullName;
        FileStream? file = new(
            Path.Combine(directory, $"{Guid.NewGuid():N}.bin"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);
        try
        {
            file.Write(bytes);
            file.Flush();
            IXRBufferSpillLease lease = Map(file, checked((uint)bytes.Length));
            file = null;
            return lease;
        }
        finally
        {
            file?.Dispose();
        }
    }

    public IXRBufferSpillLease Map(FileStream file, uint length)
    {
        // CreateFromFile failure leaves the caller's stream with the caller.
        MemoryMappedFile mapping = MemoryMappedFile.CreateFromFile(
            file, mapName: null, capacity: 0L,
            MemoryMappedFileAccess.CopyOnWrite, HandleInheritability.None,
            leaveOpen: false);
        MemoryMappedViewAccessor? view = null;
        bool pointerAcquired = false;
        try
        {
            view = mapping.CreateViewAccessor(0L, length, MemoryMappedFileAccess.CopyOnWrite);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            pointerAcquired = true;
            return new BrowserBufferSpillLease((IntPtr)(pointer + view.PointerOffset), length, mapping, view);
        }
        catch
        {
            if (pointerAcquired)
                view!.SafeMemoryMappedViewHandle.ReleasePointer();
            view?.Dispose();
            mapping.Dispose();
            throw;
        }
    }

    private string ResolveDirectory()
    {
        if (_directory is { } existing)
            return existing;

        string? root = null;
        try
        {
            root = RuntimeRenderingHostServices.Assets.GameCachePath;
        }
        catch (InvalidOperationException)
        {
        }

        string spillRoot = Path.Combine(string.IsNullOrWhiteSpace(root) ? Path.GetTempPath() : root, "BufferSpill");
        string directory = Path.Combine(spillRoot, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        _directory = directory;
        RemoveDirectoriesOfExitedProcesses(spillRoot, directory);
        return directory;
    }

    private static void RemoveDirectoriesOfExitedProcesses(string spillRoot, string directory)
    {
        foreach (string folder in Directory.EnumerateDirectories(spillRoot))
        {
            if (string.Equals(folder, directory, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                Directory.Delete(folder, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

}
