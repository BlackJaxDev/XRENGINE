namespace XREngine.Rendering.PostProcessing;

/// <summary>
/// Represents a single option for an enum-based post-processing setting.
/// </summary>
/// <param name="label">The display label for the enum option.</param>
/// <param name="value">The integer value associated with the enum option.</param>
public sealed class PostProcessEnumOption(string label, int value)
{
    /// <summary>
    /// Gets the display label for the enum option.
    /// </summary>
    public string Label { get; } = label;
    /// <summary>
    /// Gets the integer value associated with the enum option.
    /// </summary>
    public int Value { get; } = value;
}
