namespace XREngine.Rendering.Models.Materials;

/// <summary>
/// Marks an editor stencil override so the Advanced pipeline can use the
/// authored render options while the Default pipeline draws the highlight.
/// </summary>
internal sealed class LegacyEditorHighlightRenderingParameters : RenderingParameters
{
    /// <summary>The material options before the editor added stencil writes.</summary>
    internal RenderingParameters AuthoredSourceOptions { get; }

    internal LegacyEditorHighlightRenderingParameters(RenderingParameters authoredSourceOptions)
    {
        AuthoredSourceOptions = authoredSourceOptions;
    }

    /// <summary>Returns the authored options for an editor stencil override.</summary>
    internal static RenderingParameters? ResolveAuthoredOptions(RenderingParameters? options)
        => options is LegacyEditorHighlightRenderingParameters highlight
            ? highlight.AuthoredSourceOptions
            : options;
}
