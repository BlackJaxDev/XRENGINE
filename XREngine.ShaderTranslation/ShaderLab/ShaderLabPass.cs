namespace XREngine.ShaderTranslation.ShaderLab;

/// <summary>
/// Describes one pass entry of a ShaderLab subshader.
/// </summary>
public sealed class ShaderLabPass
{
    /// <summary>Gets the pass form.</summary>
    public required ShaderLabPassKind Kind { get; init; }

    /// <summary>Gets the <c>Name</c> of a normal pass, the texture name of a grab pass, or the reference of a use pass.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the pass tags. Keys compare without case.</summary>
    public required IReadOnlyDictionary<string, string> Tags { get; init; }

    /// <summary>Gets the pass render state commands in source order.</summary>
    public required IReadOnlyList<ShaderLabCommand> Commands { get; init; }

    /// <summary>Gets the program block of a normal pass. It is null for a fixed-function pass.</summary>
    public ShaderLabProgramBlock? Program { get; init; }

    /// <summary>Gets the include blocks declared inside this pass, in source order.</summary>
    public required IReadOnlyList<ShaderLabProgramBlock> IncludeBlocks { get; init; }

    /// <summary>Gets the one-based source line of the pass keyword.</summary>
    public int Line { get; init; }

    /// <summary>Gets the <c>LightMode</c> tag, or null when the pass has none.</summary>
    public string? LightMode => Tags.TryGetValue("LightMode", out string? value) ? value : null;
}
