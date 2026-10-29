using System.Text.Json.Serialization;

namespace XREngine.Rendering.Models.Gaussian.Baking.Report;

/// <summary>
/// Gives the generated JSON serializer of <see cref="BakeReport"/>. The property names come from the
/// <see cref="JsonPropertyNameAttribute"/> of each property, and null values are written.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(BakeReport))]
internal sealed partial class BakeReportJsonContext : JsonSerializerContext
{
}
