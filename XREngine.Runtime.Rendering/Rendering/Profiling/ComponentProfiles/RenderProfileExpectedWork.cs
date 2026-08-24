using System.Text.Json.Serialization;

namespace XREngine.Rendering.Profiling;

/// <summary>Optional exact counters. Omitted counters use the selected fixture's declared values.</summary>
public sealed record RenderProfileExpectedWork
{
    [JsonPropertyName("draws")]
    public long? Draws { get; init; }

    [JsonPropertyName("dispatches")]
    public long? Dispatches { get; init; }

    [JsonPropertyName("submissions")]
    public long? Submissions { get; init; }

    [JsonPropertyName("command_buffers")]
    public long? CommandBuffers { get; init; }

    [JsonPropertyName("descriptors")]
    public long? Descriptors { get; init; }

    [JsonPropertyName("barriers")]
    public long? Barriers { get; init; }

    [JsonPropertyName("upload_bytes")]
    public long? UploadBytes { get; init; }

    [JsonPropertyName("pass_iterations")]
    public long? PassIterations { get; init; }

    [JsonPropertyName("command_buffer_decisions")]
    public long? CommandBufferDecisions { get; init; }

    public void Validate()
    {
        if (Values().Any(static value => value < 0))
            throw new ArgumentOutOfRangeException(nameof(Draws), "Expected counters must be non-negative.");
    }

    private IEnumerable<long> Values()
    {
        if (Draws.HasValue) yield return Draws.Value;
        if (Dispatches.HasValue) yield return Dispatches.Value;
        if (Submissions.HasValue) yield return Submissions.Value;
        if (CommandBuffers.HasValue) yield return CommandBuffers.Value;
        if (Descriptors.HasValue) yield return Descriptors.Value;
        if (Barriers.HasValue) yield return Barriers.Value;
        if (UploadBytes.HasValue) yield return UploadBytes.Value;
        if (PassIterations.HasValue) yield return PassIterations.Value;
        if (CommandBufferDecisions.HasValue) yield return CommandBufferDecisions.Value;
    }
}
