namespace XREngine.Rendering;

/// <summary>
/// Complete contract exported by a renderer leaf assembly or collectible editor module.
/// Static applications may construct the equivalent <see cref="RendererBackendRegistration"/>
/// directly without dynamic loading.
/// </summary>
public interface IRendererBackendModule : IRendererBackendLifecycle, IDisposable
{
    RendererBackendMetadata Metadata { get; }

    IRendererBackendFactory Factory { get; }

    /// <summary>
    /// Cooperatively stops module-owned workers and callbacks after all renderer instances
    /// have been quiesced and destroyed.
    /// </summary>
    ValueTask PrepareForUnloadAsync(
        RendererModuleUnloadContext context,
        CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
