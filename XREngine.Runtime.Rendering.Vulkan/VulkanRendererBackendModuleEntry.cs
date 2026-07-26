using System.Reflection;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.XeSS;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Parameterless entry point used by collectible editor generations.
/// </summary>
public sealed class VulkanRendererBackendModuleEntry : IRendererBackendModule
{
    private IDisposable? _registrations;

    public RendererBackendMetadata Metadata { get; } = CreateMetadata();

    public IRendererBackendFactory Factory { get; } = new VulkanRendererBackendFactory();

    public void OnRegistered()
    {
        if (_registrations is not null)
            return;

        IDisposable textureLease = TextureStreamingBackendRegistry.Register(
            RuntimeGraphicsApiKind.Vulkan,
            VulkanTextureStreamingBackendProvider.Instance);
        IDisposable? vendorLease = null;
        try
        {
            vendorLease = RuntimeVendorUpscaleService.Register(VulkanVendorUpscaleService.Instance);
            IDisposable openXrLease = OpenXrGraphicsBindingRegistry.Register(
                RendererBackendId.Vulkan,
                static () => new VulkanXrGraphicsBinding());
            _registrations = new CompositeModuleRegistrationLease(
                textureLease,
                new CompositeModuleRegistrationLease(vendorLease, openXrLease));
        }
        catch
        {
            vendorLease?.Dispose();
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
        NvidiaDlssManager.Native.PrepareForModuleUnload();
        IntelXessManager.Native.PrepareForModuleUnload();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
        => OnUnregistered();

    internal static RendererBackendMetadata CreateMetadata(Version? version = null, long generation = 0)
    {
        Assembly assembly = typeof(VulkanRendererBackendModuleEntry).Assembly;
        return new(
            RendererBackendId.Vulkan,
            RuntimeGraphicsApiKind.Vulkan,
            "XREngine Vulkan",
            version ?? assembly.GetName().Version ?? new Version(1, 0),
            RendererBackendCapabilities.DesktopPresentation |
            RendererBackendCapabilities.HeadlessRendering |
            RendererBackendCapabilities.OpenXrPresentation |
            RendererBackendCapabilities.GpuCompute |
            RendererBackendCapabilities.EditorTextureInterop,
            RendererBackendReloadLimitations.RequiresRendererTeardown |
            RendererBackendReloadLimitations.NativeLoaderIsProcessScoped |
            RendererBackendReloadLimitations.RequiresOpenXrSessionTeardown,
            "Destroy all Vulkan renderer instances and OpenXR sessions before replacing this module. " +
            "The Vulkan native loader remains process scoped.",
            generation: generation,
            buildHash: RendererBackendModuleIdentity.GetBuildHash(assembly),
            targetFramework: RendererBackendModuleIdentity.GetTargetFramework(assembly),
            entryPointTypeName: typeof(VulkanRendererBackendModuleEntry).FullName);
    }
}
