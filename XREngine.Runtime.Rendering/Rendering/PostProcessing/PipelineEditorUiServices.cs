namespace XREngine.Rendering.PostProcessing;

/// <summary>Creates optional pipeline editor controls from statically registered host capabilities.</summary>
public static class PipelineEditorUiServices
{
    private static readonly object Sync = new();
    private static readonly Dictionary<Type, Func<RenderPipeline, IRenderPipelineEditorUIProvider>> Factories = [];

    public static void Register<T>(Func<T, IRenderPipelineEditorUIProvider> factory) where T : RenderPipeline
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (Sync)
            Factories[typeof(T)] = pipeline => factory((T)pipeline);
    }

    public static IRenderPipelineEditorUIProvider? Create(RenderPipeline pipeline)
    {
        Func<RenderPipeline, IRenderPipelineEditorUIProvider>? factory;
        lock (Sync)
            Factories.TryGetValue(pipeline.GetType(), out factory);
        return factory?.Invoke(pipeline);
    }
}
