using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace XREngine.Rendering.Models.Gaussian.Baking.Report;

/// <summary>Writes a <see cref="BakeReport"/> as indented JSON (schema version 1).</summary>
public static class BakeReportWriter
{
    /// <summary>The name of the report file in the output folder.</summary>
    public const string FileName = "report.json";

    /// <summary>
    /// The serializer of the report. The relaxed encoder keeps characters such as <c>+</c> in version text as they are,
    /// because the file is data, not HTML.
    /// </summary>
    private static readonly BakeReportJsonContext Context = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>Returns the report as JSON text.</summary>
    public static string Serialize(BakeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, Context.BakeReport);
    }

    /// <summary>Writes the report as <see cref="FileName"/> in <paramref name="directory"/>, and returns the path of the file.</summary>
    /// <param name="report">The report.</param>
    /// <param name="directory">The folder. It must exist.</param>
    public static string Write(BakeReport report, string directory)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        string path = Path.Combine(directory, FileName);
        File.WriteAllText(path, Serialize(report) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
