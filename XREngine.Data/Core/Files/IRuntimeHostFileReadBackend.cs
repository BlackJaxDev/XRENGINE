namespace XREngine.Data;

/// <summary>Provides direct host-file probes and streaming reads for asset operations.</summary>
public interface IRuntimeHostFileReadBackend
{
    /// <summary>Tests whether a host file exists.</summary>
    bool FileExists(string path);

    /// <summary>Opens a file for reading with the requested sharing mode. The caller disposes the returned stream.</summary>
    Stream OpenRead(string path, FileShare share);

    /// <summary>Reads all bytes from a host file.</summary>
    byte[] ReadAllBytes(string path);

    /// <summary>Reads lines lazily. Native hosts match File.ReadLines: UTF-8 by default, BOM detection, FileShare.Read, and enumerator disposal.</summary>
    IEnumerable<string> ReadLines(string path);
}
