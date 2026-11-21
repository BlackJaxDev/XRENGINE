namespace XREngine.Rendering;

/// <summary>Form of the TSR resolve that an Advanced pipeline records.</summary>
public enum EAdvancedTsrResolveForm
{
    /// <summary>One resolve pass with the coverage path on every output pixel.</summary>
    Single,
    /// <summary>
    /// Edge stencil split: an edge mask pass, a stencil classify pass, a lean resolve on non-edge
    /// pixels and the full resolve on edge pixels.
    /// </summary>
    EdgeStencilSplit,
}
