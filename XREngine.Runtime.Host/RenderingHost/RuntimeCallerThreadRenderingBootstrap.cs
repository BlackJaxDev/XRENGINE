using XREngine.Rendering;
using XREngine.Scene.Physics;
using XREngine.Components.Movement;

namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Installs the shared rendered-world services for a caller-owned output. Platform backend
/// modules, physics and the pipeline recipe are supplied explicitly by the application.
/// </summary>
public static class RuntimeCallerThreadRenderingBootstrap
{
    public static IDisposable Install(
        IRendererBackendCatalog renderers,
        PhysicsBackendCatalog physics,
        Func<IRuntimeRenderPipelineHost?> createDefaultPipeline)
    {
        ArgumentNullException.ThrowIfNull(renderers);
        ArgumentNullException.ThrowIfNull(physics);
        ArgumentNullException.ThrowIfNull(createDefaultPipeline);

        IRuntimeRenderObjectServices? previousRenderObjects = RuntimeRenderObjectServices.Current;
        IRuntimeCharacterMovementVisualizationServices? previousCharacterVisualization =
            RuntimeCharacterMovementVisualizationServices.Current;
        EngineRuntimeRenderObjectServices installedRenderObjects = new();
        RenderingCharacterMovementVisualizationServices installedCharacterVisualization = new();
        EngineRuntimeRenderingHostServices renderingHost = new(renderers, physics, createDefaultPipeline);
        IDisposable? shaderLease = null;
        IDisposable? renderingLease = null;
        try
        {
            RuntimeRenderObjectServices.Current = installedRenderObjects;
            RuntimeCharacterMovementVisualizationServices.Current = installedCharacterVisualization;
            shaderLease = EngineRuntimeShaderServices.Install();
            renderingLease = RuntimeRenderingHostServices.Install(renderingHost);
            return new Installation(renderingHost, renderingLease, shaderLease,
                installedRenderObjects, previousRenderObjects,
                installedCharacterVisualization, previousCharacterVisualization);
        }
        catch
        {
            renderingLease?.Dispose();
            shaderLease?.Dispose();
            if (ReferenceEquals(RuntimeRenderObjectServices.Current, installedRenderObjects))
                RuntimeRenderObjectServices.Current = previousRenderObjects;
            if (ReferenceEquals(RuntimeCharacterMovementVisualizationServices.Current, installedCharacterVisualization))
                RuntimeCharacterMovementVisualizationServices.Current = previousCharacterVisualization;
            renderingHost.Dispose();
            throw;
        }
    }

    private sealed class Installation(
        EngineRuntimeRenderingHostServices renderingHost,
        IDisposable renderingLease,
        IDisposable shaderLease,
        IRuntimeRenderObjectServices installedRenderObjects,
        IRuntimeRenderObjectServices? previousRenderObjects,
        IRuntimeCharacterMovementVisualizationServices installedCharacterVisualization,
        IRuntimeCharacterMovementVisualizationServices? previousCharacterVisualization) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            List<Exception>? failures = null;
            Try(renderingLease.Dispose, ref failures);
            Try(shaderLease.Dispose, ref failures);
            if (ReferenceEquals(RuntimeRenderObjectServices.Current, installedRenderObjects))
                RuntimeRenderObjectServices.Current = previousRenderObjects;
            if (ReferenceEquals(RuntimeCharacterMovementVisualizationServices.Current, installedCharacterVisualization))
                RuntimeCharacterMovementVisualizationServices.Current = previousCharacterVisualization;
            Try(renderingHost.Dispose, ref failures);
            if (failures is [Exception failure])
                throw failure;
            if (failures is { Count: > 1 })
                throw new AggregateException("Caller-thread rendering services failed to tear down.", failures);
        }

        private static void Try(Action action, ref List<Exception>? failures)
        {
            try { action(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
    }
}
