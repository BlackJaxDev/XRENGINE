using System.Text.Json.Serialization;

namespace XREngine.Rendering.Profiling;

/// <summary>Declared measurement boundary and validity requirements for a fixture.</summary>
public sealed record RenderProfileFixtureContract
{
    [JsonPropertyName("inclusions")]
    public string[] Inclusions { get; init; } = [];

    [JsonPropertyName("exclusions")]
    public string[] Exclusions { get; init; } = [];

    [JsonPropertyName("validity_requirements")]
    public string[] ValidityRequirements { get; init; } = [];

    public void Validate()
    {
        if (Inclusions is null || Exclusions is null || ValidityRequirements is null)
            throw new ArgumentException("Fixture contract lists may not be null.");
        if (Inclusions.Any(string.IsNullOrWhiteSpace) || Exclusions.Any(string.IsNullOrWhiteSpace) || ValidityRequirements.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Fixture contract lists may not contain empty values.");
    }
}
