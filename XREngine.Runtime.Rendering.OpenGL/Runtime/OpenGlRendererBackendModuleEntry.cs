using System.Reflection;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// Parameterless entry point used by collectible editor generations.
/// </summary>
public sealed class OpenGlRendererBackendModuleEntry : IRendererBackendModule
{
    private IDisposable? _registrations;

    public RendererBackendMetadata Metadata { get; } = CreateMetadata();

    public IRendererBackendFactory Factory { get; } = new OpenGLRendererBackendFactory();

    public void OnRegistered()
    {
        if (_registrations is not null)
            return;

        IDisposable textureLease = TextureStreamingBackendRegistry.Register(
            RuntimeGraphicsApiKind.OpenGL,
            OpenGlTextureStreamingBackendProvider.Instance);
        try
        {
            IDisposable openXrLease = OpenXrGraphicsBindingRegistry.Register(
                RendererBackendId.OpenGL,
                static () => new OpenGlXrGraphicsBinding());
            _registrations = new CompositeModuleRegistrationLease(textureLease, openXrLease);
        }
        catch
        {
            textureLease.Dispose();
            throw;
        }
    }

    public void OnUnregistered()
        => Interlocked.Exchange(ref _registrations, null)?.Dispose();

    public ValueTask PrepareForUnloadAsync(
        RendererModuleUnloadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnUnregistered();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
        => OnUnregistered();

    internal static RendererBackendMetadata CreateMetadata(Version? version = null, long generation = 0)
    {
        Assembly assembly = typeof(OpenGlRendererBackendModuleEntry).Assembly;
        return new(
            RendererBackendId.OpenGL,
            RuntimeGraphicsApiKind.OpenGL,
            "XREngine OpenGL",
            version ?? assembly.GetName().Version ?? new Version(1, 0),
            RendererBackendCapabilities.DesktopPresentation |
            RendererBackendCapabilities.HeadlessRendering |
            RendererBackendCapabilities.OpenXrPresentation |
            RendererBackendCapabilities.GpuCompute |
            RendererBackendCapabilities.EditorTextureInterop |
            RendererBackendCapabilities.SparseTextureStreaming,
            RendererBackendReloadLimitations.RequiresRendererTeardown |
            RendererBackendReloadLimitations.NativeLoaderIsProcessScoped |
            RendererBackendReloadLimitations.RequiresOpenXrSessionTeardown,
            "Destroy all renderer instances and OpenXR sessions before replacing this module. " +
            "The native graphics loader remains process scoped.",
            generation: generation,
            buildHash: RendererBackendModuleIdentity.GetBuildHash(assembly),
            targetFramework: RendererBackendModuleIdentity.GetTargetFramework(assembly),
            entryPointTypeName: typeof(OpenGlRendererBackendModuleEntry).FullName);
    }
}

