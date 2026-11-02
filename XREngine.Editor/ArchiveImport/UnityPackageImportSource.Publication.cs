namespace XREngine.Editor;

internal static partial class UnityPackageImportSource
{
    /// <summary>Retries short Windows file locks while publishing a complete cache directory.</summary>
    private static void PublishCacheDirectory(string staging, string target)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(staging, target);
                return;
            }
            catch (IOException ex) when (OperatingSystem.IsWindows() && attempt < 9 &&
                (ex.HResult & 0xFFFF) is 5 or 32 or 33 && !Directory.Exists(target))
            {
                // File scanners can briefly retain a handle after extraction.
                // Keep the completed staging tree intact until the retry ends.
                Thread.Sleep(Math.Min(25 << attempt, 250));
            }
        }
    }
}
