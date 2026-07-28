namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Selects the fastest safe editor update mechanism for an attempted fix.
/// </summary>
public enum SelfIterationReloadMode
{
    Auto,
    ShaderReload,
    RendererRestart,
    BuildAndReloadRenderer,
    EditorRestart,
}
