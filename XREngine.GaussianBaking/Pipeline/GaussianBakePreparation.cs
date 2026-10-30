using XREngine.Rendering.Models.Gaussian.Baking.Cli;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Rendering.Models.Gaussian.Baking.Output;
using XREngine.Rendering.Models.Gaussian.Baking.Report;
using XREngine.Rendering.Models.Gaussian.Baking.Video;
using XREngine.Scene;

namespace XREngine.Rendering.Models.Gaussian.Baking.Pipeline;

/// <summary>
/// Holds the result of the steps of a bake that do not use the renderer: the option and encoder checks, the load of
/// the input, and the content check. <see cref="GaussianBakePipeline.PrepareAsync"/> makes it, and
/// <see cref="GaussianBakePipeline.RunAsync(GaussianBakePreparation, CancellationToken)"/> continues the bake with it.
/// </summary>
/// <remarks>
/// The editor command prepares the bake before the engine starts, so that a load failure exits with its failure code
/// before the engine starts. A preparation that failed already holds the failed report, and the output folder holds
/// that report when <see cref="ReportPath"/> is not null.
/// </remarks>
public sealed class GaussianBakePreparation
{
    private readonly BakeOutputDirectory? _output;

    internal GaussianBakePreparation(BakeSplatPreviewOptions options, BakeReport report, BakeOutputDirectory? output)
    {
        Options = options;
        Report = report;
        _output = output;
    }

    /// <summary>Gets the bake options.</summary>
    public BakeSplatPreviewOptions Options { get; }

    /// <summary>Gets the report. Its status is <see cref="BakeReport.FailedStatus"/> when the preparation failed.</summary>
    public BakeReport Report { get; }

    /// <summary>
    /// Gets the full path of the <see cref="BakeReportWriter.FileName"/> that the bake wrote into the output folder, or
    /// null when the bake wrote no report: the bake did not end, the output folder cannot be used, or a file error
    /// stopped the write.
    /// </summary>
    public string? ReportPath { get; private set; }

    /// <summary>Gets a value that is true when the bake can continue: the checks passed and the input is loaded.</summary>
    public bool IsPrepared => Input is not null && _output is not null && !Consumed
        && !string.Equals(Report.Status, BakeReport.FailedStatus, StringComparison.Ordinal);

    /// <summary>Gets the loaded input, or null when the preparation failed.</summary>
    public CookedBakeInput? Input { get; internal init; }

    /// <summary>Gets the root nodes of the content.</summary>
    internal SceneNode[] Roots { get; init; } = [];

    /// <summary>Gets the ffmpeg encoder that passed the encoder check.</summary>
    internal FfmpegEncoder? Encoder { get; init; }

    /// <summary>Gets the output folder of the bake. It is null when the staging folder cannot be made.</summary>
    internal BakeOutputDirectory? Output => _output;

    /// <summary>Gets a value that is true after a run used this preparation or after <see cref="Fail"/>.</summary>
    internal bool Consumed { get; private set; }

    /// <summary>Marks the preparation as used by a run. A preparation can run one time.</summary>
    internal void Consume()
    {
        if (Consumed)
            throw new InvalidOperationException("A bake preparation can run one time.");
        Consumed = true;
    }

    /// <summary>
    /// Stops a bake that did not run: the report gets the failure code, the staging folder is deleted, and the output
    /// folder gets only <c>report.json</c>. A preparation that a run used or that failed does not change.
    /// </summary>
    /// <param name="failureCode">One of the values of <see cref="GaussianBakeFailureCodes"/>.</param>
    /// <param name="failureDetail">The message that explains the failure.</param>
    public void Fail(string failureCode, string failureDetail)
    {
        if (Consumed || !IsPrepared)
            return;

        Consumed = true;
        Report.MarkFailed(failureCode, failureDetail);
        WriteFailedReport();
    }

    /// <summary>Records that the commit put the outputs and the ready report into the output folder.</summary>
    internal void MarkCommitted()
        => ReportPath = Path.Combine(_output!.FinalPath, BakeReportWriter.FileName);

    /// <summary>
    /// Deletes the staging folder and replaces the output folder with a folder that holds only the failed report. A
    /// file error, or an output folder that the bake cannot replace, does not stop the caller: the method writes the
    /// error to the standard error stream, and <see cref="ReportPath"/> stays null. The report stays available to the
    /// caller.
    /// </summary>
    internal void WriteFailedReport()
    {
        ReportPath = null;
        if (_output is null)
            return;

        try
        {
            _output.Fail(Report);
            ReportPath = Path.Combine(_output.FinalPath, BakeReportWriter.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GaussianBakeException)
        {
            Console.Error.WriteLine($"Gaussian preview bake: the failed report cannot be written to '{_output.FinalPath}': {ex.Message}");
        }
    }
}
