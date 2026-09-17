using System.Text.Json.Serialization;

namespace XREngine.AgentOrchestration.TypeSafe;

// ── Questions ────────────────────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ChoiceQuestion), "choice")]
[JsonDerivedType(typeof(NoulQuestion), "noul")]
[JsonDerivedType(typeof(ScoreQuestion), "score")]
public abstract record TypeSafeQuestion
{
    [JsonPropertyName("instructions")]
    public required object Instructions { get; init; }
}

/// <summary>
/// A Choice question selects one option from a defined set.
/// Returns the chosen option, a full probability distribution, and confidence.
/// </summary>
public sealed record ChoiceQuestion : TypeSafeQuestion
{
    [JsonPropertyName("criteria")]
    public required IReadOnlyDictionary<string, string?> Criteria { get; init; }
}

/// <summary>
/// A Noul question evaluates a condition and returns the probability (0.0 to 1.0) that the answer is yes.
/// </summary>
public sealed record NoulQuestion : TypeSafeQuestion
{
    [JsonPropertyName("criteria")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Criteria { get; init; }
}

/// <summary>
/// A Score question rates state along an ordered rubric of levels (minimum 2 levels).
/// Returns a probability-weighted score across the levels, legend, probabilities, and confidence.
/// </summary>
public sealed record ScoreQuestion : TypeSafeQuestion
{
    [JsonPropertyName("criteria")]
    public required IReadOnlyList<string> Criteria { get; init; }
}

// ── Answers ──────────────────────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ChoiceAnswer), "choice")]
[JsonDerivedType(typeof(NoulAnswer), "noul")]
[JsonDerivedType(typeof(ScoreAnswer), "score")]
public abstract record TypeSafeAnswer;

public sealed record ChoiceAnswer : TypeSafeAnswer
{
    [JsonPropertyName("choice")]
    public string Choice { get; init; } = string.Empty;

    [JsonPropertyName("probabilities")]
    public IReadOnlyDictionary<string, double> Probabilities { get; init; } = new Dictionary<string, double>();

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }
}

public sealed record NoulAnswer : TypeSafeAnswer
{
    [JsonPropertyName("noul")]
    public double Noul { get; init; }
}

public sealed record ScoreAnswer : TypeSafeAnswer
{
    [JsonPropertyName("score")]
    public double Score { get; init; }

    [JsonPropertyName("legend")]
    public IReadOnlyDictionary<string, string> Legend { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("probabilities")]
    public IReadOnlyDictionary<string, double> Probabilities { get; init; } = new Dictionary<string, double>();

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }
}

// ── Request & Response Envelope ──────────────────────────────────────────

public sealed record TypeSafeUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }
}

public sealed record TypeSafeRequest
{
    [JsonPropertyName("state")]
    public required object State { get; init; }

    [JsonPropertyName("model")]
    public string Model { get; init; } = "jev-latest";

    [JsonPropertyName("questions")]
    public required IReadOnlyDictionary<string, TypeSafeQuestion> Questions { get; init; }
}

public sealed record TypeSafeResponse
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("answers")]
    public IReadOnlyDictionary<string, TypeSafeAnswer> Answers { get; init; } = new Dictionary<string, TypeSafeAnswer>();

    [JsonPropertyName("usage")]
    public TypeSafeUsage? Usage { get; init; }
}
