using System.Globalization;
using System.Text.Json;
using XREngine.Rendering.Models.Gaussian.Baking.Report;
using XREngine.Rendering.Models.Gaussian.Baking.Video;

namespace XREngine.Rendering.Models.Gaussian.Baking.Output;

/// <summary>
/// Writes the outputs of a bake into a staging folder next to the final folder, and then puts them in the final
/// folder in one step. A reader of the final folder never sees a part of a bake.
/// </summary>
/// <remarks>
/// <para>The staging folder is <c>&lt;final&gt;.staging-&lt;guid&gt;</c>, in the same parent folder, so that the commit is a
/// move in one volume. File operations try again a small number of times, because a file indexer or a sync client can
/// hold a new file open for a short time.</para>
/// <para>The bake replaces the final folder only when the folder does not exist, is empty, or is the output of an
/// earlier bake (see <see cref="CanReplace"/>). The bake never replaces a drive root, a share root, or the user profile
/// folder. For all other folders, <see cref="Begin"/>, <see cref="Commit"/>, and <see cref="Fail"/> stop with
/// <see cref="GaussianBakeFailureCodes.InvalidArguments"/> before they delete a file, and they write nothing into the
/// folder.</para>
/// </remarks>
public sealed class BakeOutputDirectory
{
    /// <summary>The text between the final path and the unique part of the staging folder name.</summary>
    public const string StagingSuffix = ".staging-";

    /// <summary>The folder in the output folder that receives the capture views of <c>--dump-views-only</c>.</summary>
    public const string DebugFolderName = "debug";

    /// <summary>The largest <see cref="BakeReportWriter.FileName"/> that the check reads. A bake report has a few kilobytes.</summary>
    private const long MaxReportBytes = 1L << 20;

    /// <summary>The bake kinds that a report of a bake can give.</summary>
    private static readonly string[] ReportKinds = ["avatar", "item", "world"];

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
    /// <exception cref="GaussianBakeException">
    /// The bake cannot replace the final folder (<see cref="GaussianBakeFailureCodes.InvalidArguments"/>). No folder is made.
    /// </exception>
    /// <exception cref="IOException">The staging folder cannot be made.</exception>
    public static BakeOutputDirectory Begin(string finalDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalDirectory);

        string finalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(finalDirectory));
        EnsureReplaceable(finalPath);
        string stagingPath = finalPath + StagingSuffix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(stagingPath);
        return new BakeOutputDirectory(finalPath, stagingPath);
    }

    /// <summary>
    /// Gets the names of the files that a bake writes at the top level of its output folder: the report, the loop videos,
    /// and the posters. A folder whose top level has an entry that is not in this list or in
    /// <see cref="OutputFolderNames"/> is not the output of a bake.
    /// </summary>
    public static IReadOnlyList<string> OutputFileNames { get; } =
        [BakeReportWriter.FileName, .. LoopRendition.All.Select(rendition => rendition.FileName)];

    /// <summary>Gets the names of the folders that a bake writes at the top level of its output folder.</summary>
    public static IReadOnlyList<string> OutputFolderNames { get; } = [BakeCacheFiles.FolderName, DebugFolderName];

    /// <summary>
    /// Returns true when a bake can replace the folder: the folder does not exist, it is empty, or it is the output of
    /// an earlier bake. The output of a bake has a top-level <see cref="BakeReportWriter.FileName"/> that is a bake report
    /// (schema version <see cref="BakeReport.CurrentSchemaVersion"/>, a known status, and a known kind), and every other
    /// top-level entry has a name of <see cref="OutputFileNames"/> or <see cref="OutputFolderNames"/>. A drive root, a
    /// share root, and the user profile folder are never replaceable.
    /// </summary>
    /// <param name="finalDirectory">The final output folder.</param>
    public static bool CanReplace(string finalDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalDirectory);
        return FindRefusal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(finalDirectory))) is null;
    }

    /// <summary>Returns why a bake cannot replace the folder, or null when it can.</summary>
    private static string? FindRefusal(string finalPath)
    {
        if (IsProtectedFolder(finalPath))
            return "is a drive root, a share root, or the user profile folder";
        if (File.Exists(finalPath))
            return "is a file";
        if (!Directory.Exists(finalPath))
            return null;

        bool hasEntries = false;
        foreach (string entry in Directory.EnumerateFileSystemEntries(finalPath))
        {
            hasEntries = true;
            string name = Path.GetFileName(entry);
            bool known = Directory.Exists(entry)
                ? OutputFolderNames.Contains(name, StringComparer.Ordinal)
                : OutputFileNames.Contains(name, StringComparer.Ordinal);
            if (!known)
                return $"holds '{name}', which a bake does not write, so it is not the output of an earlier bake";
        }

        if (hasEntries && !IsBakeReport(Path.Combine(finalPath, BakeReportWriter.FileName)))
            return $"is not empty and does not hold the {BakeReportWriter.FileName} of an earlier bake";
        return null;
    }

    /// <summary>Returns true for a drive root, a share root, and the user profile folder.</summary>
    private static bool IsProtectedFolder(string finalPath)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? root = Path.GetPathRoot(finalPath);
        if (!string.IsNullOrEmpty(root) && string.Equals(Path.TrimEndingDirectorySeparator(root), finalPath, comparison))
            return true;

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(profile)
            && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile)), finalPath, comparison);
    }

    /// <summary>
    /// Returns true when the file is a bake report: a JSON object of at most <see cref="MaxReportBytes"/> bytes with
    /// <c>schemaVersion</c> <see cref="BakeReport.CurrentSchemaVersion"/>, a <c>status</c> of a bake, and a <c>kind</c>
    /// of a bake.
    /// </summary>
    private static bool IsBakeReport(string path)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length > MaxReportBytes)
                return false;

            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("schemaVersion", out JsonElement version)
                && version.ValueKind == JsonValueKind.Number
                && version.TryGetInt32(out int schemaVersion)
                && schemaVersion == BakeReport.CurrentSchemaVersion
                && IsOneOf(root, "status", [BakeReport.ReadyStatus, BakeReport.FailedStatus])
                && IsOneOf(root, "kind", ReportKinds);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsOneOf(JsonElement root, string property, string[] values)
        => root.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && values.Contains(value.GetString(), StringComparer.Ordinal);

    /// <summary>
    /// Deletes the final folder with all the files in it, and then moves the staging folder into its place.
    /// </summary>
    /// <exception cref="GaussianBakeException">
    /// The bake cannot replace the final folder (<see cref="GaussianBakeFailureCodes.InvalidArguments"/>). Nothing is deleted.
    /// </exception>
    /// <exception cref="IOException">The final folder cannot be deleted or the staging folder cannot be moved.</exception>
    public void Commit()
    {
        EnsureReplaceable(FinalPath);
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
    /// <exception cref="GaussianBakeException">
    /// The bake cannot replace the final folder (<see cref="GaussianBakeFailureCodes.InvalidArguments"/>). The staging
    /// folder is deleted, and the final folder does not change.
    /// </exception>
    /// <exception cref="IOException">A folder cannot be deleted or the report cannot be written.</exception>
    public void Fail(BakeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Retry(() =>
        {
            if (Directory.Exists(StagingPath))
                Directory.Delete(StagingPath, recursive: true);
        });
        EnsureReplaceable(FinalPath);
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

    /// <summary>Stops with <see cref="GaussianBakeFailureCodes.InvalidArguments"/> when a bake cannot replace the folder.</summary>
    private static void EnsureReplaceable(string finalPath)
    {
        string? reason = FindRefusal(finalPath);
        if (reason is null)
            return;

        throw new GaussianBakeException(
            GaussianBakeFailureCodes.InvalidArguments,
            $"The output folder '{finalPath}' {reason}. Give a new folder, an empty folder, or the output folder of an earlier bake. The bake did not change it.");
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
