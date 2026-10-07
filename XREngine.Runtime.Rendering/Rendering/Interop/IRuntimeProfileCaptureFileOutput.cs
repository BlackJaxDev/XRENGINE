namespace XREngine.Rendering;

/// <summary>Writes speed profile captures through a host file output service.</summary>
public interface IRuntimeProfileCaptureFileOutput
{
    /// <summary>Creates a directory for speed profile output.</summary>
    void EnsureProfileDirectory(string directoryPath);

    /// <summary>Deletes old speed profile directories within the profile root.</summary>
    void EnforceProfileRetention(string profileRoot, int retainedCount);

    /// <summary>Appends or replaces speed profile text with UTF-8 encoding.</summary>
    void WriteProfileText(string filePath, string contents, bool append);
}
