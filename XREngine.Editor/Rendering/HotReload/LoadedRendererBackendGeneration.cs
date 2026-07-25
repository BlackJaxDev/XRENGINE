using System.Runtime.Loader;
using XREngine.Rendering;

namespace XREngine.Editor.HotReload;

public sealed class LoadedRendererBackendGeneration
{
    private RendererBackendLoadContext? _loadContext;
    private IRendererBackendModule? _module;
    private RendererBackendRegistration? _registration;

    internal LoadedRendererBackendGeneration(
        RendererBackendGenerationManifest manifest,
        string manifestPath,
        RendererBackendLoadContext loadContext,
        IRendererBackendModule module,
        RendererBackendRegistration registration)
    {
        Manifest = manifest;
        ManifestPath = manifestPath;
        _loadContext = loadContext;
        _module = module;
        _registration = registration;
        LoadContextName = loadContext.Name ?? string.Empty;
    }

    public RendererBackendGenerationManifest Manifest { get; }
    public string ManifestPath { get; }
    public RendererBackendRegistration Registration
        => _registration ?? throw new ObjectDisposedException(nameof(LoadedRendererBackendGeneration));
    public string LoadContextName { get; }

    public WeakReference BeginUnload()
    {
        RendererBackendLoadContext? context = Interlocked.Exchange(ref _loadContext, null);
        IRendererBackendModule? module = Interlocked.Exchange(ref _module, null);
        Interlocked.Exchange(ref _registration, null);
        module?.Dispose();
        if (context is null)
            return new WeakReference(null);

        WeakReference weakReference = new(context, trackResurrection: false);
        context.Unload();
        return weakReference;
    }
}
