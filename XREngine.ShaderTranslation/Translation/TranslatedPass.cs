using XREngine.ShaderTranslation.ShaderLab;

namespace XREngine.ShaderTranslation.Translation;

/// <summary>
/// Holds one translated pass: its engine use, render state, and stages.
/// </summary>
public sealed class TranslatedPass
{
    /// <summary>Gets the zero-based pass index in its subshader.</summary>
    public required int Index { get; init; }

    /// <summary>Gets the pass <c>Name</c>, or null.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the engine use of the pass.</summary>
    public required EnginePassKind Kind { get; init; }

    /// <summary>Gets the subshader commands followed by the pass commands. Later commands override earlier ones.</summary>
    public required IReadOnlyList<ShaderLabCommand> Commands { get; init; }

    /// <summary>Gets the merged subshader and pass tags. Pass tags win.</summary>
    public required IReadOnlyDictionary<string, string> Tags { get; init; }

    /// <summary>Gets the translated stages in pipeline order.</summary>
    public required IReadOnlyList<TranslatedStage> Stages { get; init; }

    /// <summary>Returns a short label for diagnostics, such as <c>#1 FORWARD</c>.</summary>
    public string Label => Name is null ? $"#{Index}" : $"#{Index} {Name}";
}
