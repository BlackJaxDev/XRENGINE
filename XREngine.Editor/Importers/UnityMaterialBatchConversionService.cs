using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.Scene.Importers.Poiyomi;

namespace XREngine.Scene.Importers;

public sealed record UnityMaterialBatchItem(
    string SourceAssetPath,
    MaterialConversionReport Report);

public sealed record UnityMaterialBatchProgress(
    int Completed,
    int Total,
    string SourceAssetPath,
    EMaterialConversionOutcome Outcome);

/// <summary>
/// Deterministic aggregate suitable for project/avatar audits and CI artifacts.
/// </summary>
public sealed class UnityMaterialBatchReport
{
    public int FormatVersion { get; init; } = 1;
    public string ConverterId { get; init; } = MaterialConversionReportBuilder.ConverterId;
    public string ConverterVersion { get; init; } = MaterialConversionReportBuilder.ConverterVersion;
    public int SourceDescriptorVersion { get; init; } = MaterialConversionReportBuilder.SourceDescriptorVersion;
    public IReadOnlyList<UnityMaterialBatchItem> Materials { get; init; } = [];
    public int ConvertedMaterials { get; init; }
    public int GenericFallbackMaterials { get; init; }
    public int FailedMaterials { get; init; }
    public MaterialConversionCounters Counters { get; init; } = new(0, 0, 0, 0, 0, 0);

    public string ToJson(bool indented = true)
        => JsonSerializer.Serialize(
            this,
            new JsonSerializerOptions
            {
                WriteIndented = indented,
                Converters = { new JsonStringEnumConverter() },
            });
}

public static class UnityMaterialBatchConversionService
{
    public static Task<UnityMaterialBatchReport> AuditProjectAsync(
        string projectRoot,
        IProgress<UnityMaterialBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => AuditAsync([projectRoot], recursive: true, progress, cancellationToken);

    public static Task<UnityMaterialBatchReport> AuditAvatarAsync(
        IEnumerable<string> materialPaths,
        IProgress<UnityMaterialBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => AuditAsync(materialPaths, recursive: false, progress, cancellationToken);

    public static async Task<UnityMaterialBatchReport> AuditAsync(
        IEnumerable<string> rootsOrMaterialPaths,
        bool recursive,
        IProgress<UnityMaterialBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootsOrMaterialPaths);
        string[] paths = DiscoverMaterials(rootsOrMaterialPaths, recursive);
        List<UnityMaterialBatchItem> items = new(paths.Length);
        for (int index = 0; index < paths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = paths[index];
            MaterialConversionReport report;
            try
            {
                UnityMaterialImportResult result =
                    await Task.Run(() => UnityMaterialImporter.ImportWithReport(path), cancellationToken)
                        .ConfigureAwait(false);
                report = result.ConversionReport ??
                         MaterialConversionReportBuilder.CreateFailure(
                             path,
                             "The importer did not return a conversion report.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                report = MaterialConversionReportBuilder.CreateFailure(path, exception.Message);
            }

            items.Add(new(path, report));
            progress?.Report(new(index + 1, paths.Length, path, report.Outcome));
        }

        MaterialConversionReport[] reports = items.Select(static item => item.Report).ToArray();
        return new()
        {
            Materials = items,
            ConvertedMaterials = reports.Count(static report =>
                report.Outcome == EMaterialConversionOutcome.Converted),
            GenericFallbackMaterials = reports.Count(static report =>
                report.Outcome == EMaterialConversionOutcome.GenericFallback),
            FailedMaterials = reports.Count(static report =>
                report.Outcome == EMaterialConversionOutcome.Failed),
            Counters = new(
                reports.Sum(static report => report.Counters.EnabledSourceFeatures),
                reports.Sum(static report => report.Counters.GeneratedFeatures),
                reports.Sum(static report => report.Counters.SamplerPressure),
                reports.Sum(static report => report.Counters.GeneratedVariants),
                reports.Sum(static report => report.Counters.GeneratedPasses),
                reports.Sum(static report => report.Counters.UnsupportedIntegrations)),
        };
    }

    public static async Task WriteJsonAsync(
        UnityMaterialBatchReport report,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(fullPath, report.ToJson(), cancellationToken).ConfigureAwait(false);
    }

    private static string[] DiscoverMaterials(IEnumerable<string> rootsOrMaterialPaths, bool recursive)
    {
        SortedSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in rootsOrMaterialPaths)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            string fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                if (string.Equals(Path.GetExtension(fullPath), ".mat", StringComparison.OrdinalIgnoreCase))
                    paths.Add(fullPath);
                continue;
            }
            if (!Directory.Exists(fullPath))
                continue;

            foreach (string path in Directory.EnumerateFiles(
                         fullPath,
                         "*.mat",
                         recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                paths.Add(Path.GetFullPath(path));
        }
        return [.. paths];
    }
}
