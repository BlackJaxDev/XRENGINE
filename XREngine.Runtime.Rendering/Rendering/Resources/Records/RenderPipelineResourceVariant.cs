namespace XREngine.Rendering.Resources;

/// <summary>
/// Lossless, pipeline-defined structural settings carried from a resource request
/// into its factories. Equality distinguishes resource shapes without live-state reads.
/// </summary>
public readonly record struct RenderPipelineResourceVariant(ulong Word0, ulong Word1, ulong Word2, ulong Word3);
