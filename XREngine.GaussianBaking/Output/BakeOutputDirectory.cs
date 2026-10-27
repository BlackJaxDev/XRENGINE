using System.Globalization;
using XREngine.Rendering.Models.Gaussian.Baking.Report;

namespace XREngine.Rendering.Models.Gaussian.Baking.Output;

/// <summary>
/// Writes the outputs of a bake into a staging folder next to the final folder, and then puts them in the final
/// folder in one step. A reader of the final folder never sees a part of a bake.
/// </summary>
/// <remarks>
/// The staging folder is <c>&lt;final&gt;.staging-&lt;guid&gt;</c>, in the same parent folder, so that the commit is a
/// move in one volume. File operations try again a small number of times, because a file indexer or a sync client can
/// hold a new file open for a short time.
/// </remarks>
public sealed class BakeOutputDirectory
{
    /// <summary>The text between the final path and the unique part of the staging folder name.</summary>
    public const string StagingSuffix = ".staging-";

    private const int MaxAttempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    private BakeOutputDirectory(string finalPath, string stagingPath)
    {
        FinalPath = finalPath;
        StagingPath = stagingPath;
    }

    /// <summary>Gets the full path of the final output folder.</summary>
    public string FinalPath { get; }

    /// <summary>Gets the full path of the staging folder that receives the outputs.</summary>
    public string StagingPath { get; }

    /// <summary>Makes a new, empty staging folder for <paramref name="finalDirectory"/>.</summary>
    /// <param name="finalDirectory">The final output folder. It does not have to exist.</param>
    /// <exception cref="IOException">The staging folder cannot be made.</exception>
    public static BakeOutputDirectory Begin(string finalDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalDirectory);

        string finalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(finalDirectory));
        string stagingPath = finalPath + StagingSuffix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(stagingPath);
        return new BakeOutputDirectory(finalPath, stagingPath);
    }

    /// <summary>
    /// Deletes the final folder with all the files in it, and then moves the staging folder into its place.
    /// </summary>
    /// <exception cref="IOException">The final folder cannot be deleted or the staging folder cannot be moved.</exception>
    public void Commit()
    {
        Retry(() =>
        {
            if (Directory.Exists(FinalPath))
                Directory.Delete(FinalPath, recursive: true);
        });
        Retry(() => Directory.Move(StagingPath, FinalPath));
    }

    /// <summary>
    /// Deletes the staging folder, and then replaces the final folder with a folder that holds only
    /// <see cref="BakeReportWriter.FileName"/> with <paramref name="report"/>.
    /// </summary>
    /// <param name="report">The report of the failed bake.</param>
    /// <exception cref="IOException">A folder cannot be deleted or the report cannot be written.</exception>
    public void Fail(BakeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Retry(() =>
        {
            if (Directory.Exists(StagingPath))
                Directory.Delete(StagingPath, recursive: true);
        });
        Retry(() =>
        {
            if (Directory.Exists(FinalPath))
                Directory.Delete(FinalPath, recursive: true);
        });
        Retry(() =>
        {
            Directory.CreateDirectory(FinalPath);
            BakeReportWriter.Write(report, FinalPath);
        });
    }

    /// <summary>Runs a file operation, and runs it again after a short wait when it fails with an access or IO error.</summary>
    private static void Retry(Action operation)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }
}
