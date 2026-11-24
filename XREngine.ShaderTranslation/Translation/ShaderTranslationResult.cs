using XREngine.ShaderTranslation.Diagnostics;
using XREngine.ShaderTranslation.ShaderLab;

namespace XREngine.ShaderTranslation.Translation;

/// <summary>
/// Holds the translated passes of one shader and the diagnostics of the translation.
/// </summary>
public sealed class ShaderTranslationResult
{
    /// <summary>Gets the parsed shader, or null when parsing failed.</summary>
    public ShaderLabDocument? Document { get; init; }

    /// <summary>Gets the translated passes in source order.</summary>
    public required IReadOnlyList<TranslatedPass> Passes { get; init; }

    /// <summary>Gets all diagnostics in the order they were found.</summary>
    public required IReadOnlyList<ShaderTranslationDiagnostic> Diagnostics { get; init; }

    /// <summary>Returns true when no diagnostic is an error.</summary>
    public bool Succeeded
    {
        get
        {
            foreach (ShaderTranslationDiagnostic diagnostic in Diagnostics)
                if (diagnostic.Severity == ShaderTranslationSeverity.Error)
                    return false;
            return true;
        }
    }
}
