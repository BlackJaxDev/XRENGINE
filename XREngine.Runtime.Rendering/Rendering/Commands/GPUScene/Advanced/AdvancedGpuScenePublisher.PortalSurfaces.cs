using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Commands;

public sealed partial class AdvancedGpuScenePublisher
{
    private const int InitialPortalSurfaceCapacity = 16;

    private readonly object _portalSurfaceSync = new();
    private IAdvancedPortalSurfaceProvider?[] _registeredPortalProviders =
        new IAdvancedPortalSurfaceProvider?[InitialPortalSurfaceCapacity];
    private AdvancedGpuHandle[] _registeredPortalDrawHandles =
        new AdvancedGpuHandle[InitialPortalSurfaceCapacity];
    private int _registeredPortalProviderCount;

    /// <summary>Number of currently registered portal and mirror surface providers.</summary>
    public int RegisteredPortalSurfaceCount
    {
        get
        {
            lock (_portalSurfaceSync)
                return _registeredPortalProviderCount;
        }
    }

    /// <summary>
    /// Registers an in-place portal or mirror surface provider with this scene publisher.
    /// Re-registering an existing provider updates its associated draw handle without allocating.
    /// </summary>
    public void RegisterPortalSurface(
        IAdvancedPortalSurfaceProvider provider,
        AdvancedGpuHandle drawHandle = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_portalSurfaceSync)
        {
            int existingIndex = FindRegisteredPortalProviderIndexLocked(provider);
            if (existingIndex >= 0)
            {
                if (drawHandle.IsValid)
                    _registeredPortalDrawHandles[existingIndex] = drawHandle;
                return;
            }

            if (_registeredPortalProviderCount == _registeredPortalProviders.Length)
            {
                int newCapacity = _registeredPortalProviders.Length * 2;
                Array.Resize(ref _registeredPortalProviders, newCapacity);
                Array.Resize(ref _registeredPortalDrawHandles, newCapacity);
            }

            _registeredPortalProviders[_registeredPortalProviderCount] = provider;
            _registeredPortalDrawHandles[_registeredPortalProviderCount] = drawHandle;
            ++_registeredPortalProviderCount;
        }
    }

    /// <summary>
    /// Removes a portal or mirror surface provider from this scene publisher and clears its
    /// retained reference so destroyed components cannot leak.
    /// </summary>
    public bool UnregisterPortalSurface(IAdvancedPortalSurfaceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_portalSurfaceSync)
        {
            int index = FindRegisteredPortalProviderIndexLocked(provider);
            if (index < 0)
                return false;

            int lastIndex = --_registeredPortalProviderCount;
            if (index != lastIndex)
            {
                _registeredPortalProviders[index] = _registeredPortalProviders[lastIndex];
                _registeredPortalDrawHandles[index] = _registeredPortalDrawHandles[lastIndex];
            }

            _registeredPortalProviders[lastIndex] = null;
            _registeredPortalDrawHandles[lastIndex] = default;
            return true;
        }
    }

    /// <summary>
    /// Captures all currently active portal surface descriptors into caller-owned storage
    /// without heap allocation.
    /// </summary>
    public int CaptureActivePortalSurfaces(Span<AdvancedPortalSurfaceDescriptor> destination)
    {
        int written = 0;
        lock (_portalSurfaceSync)
        {
            for (int index = 0; index < _registeredPortalProviderCount && written < destination.Length; ++index)
            {
                IAdvancedPortalSurfaceProvider? provider = _registeredPortalProviders[index];
                if (provider is null || !provider.IsPortalSurfaceActive)
                    continue;
                if (!provider.TryCapturePortalSurface(out AdvancedPortalSurfaceDescriptor descriptor))
                    continue;

                AdvancedGpuHandle publishedDraw = _registeredPortalDrawHandles[index];
                if (publishedDraw.IsValid && !descriptor.DrawHandle.IsValid)
                    descriptor = descriptor with { DrawHandle = publishedDraw };

                destination[written++] = descriptor;
            }
        }

        return written;
    }

    /// <summary>
    /// Records the published canonical draw handle for a portal surface command during
    /// scene publication, and returns whether the command carries an active portal surface.
    /// </summary>
    internal bool TryTrackPortalSurfaceCommand(
        IRenderCommandMesh? source,
        AdvancedGpuHandle drawHandle)
    {
        if (source is not RenderCommand renderCommand ||
            renderCommand.OwnerRenderInfo?.Owner is not IAdvancedPortalSurfaceProvider provider ||
            !provider.IsPortalSurfaceActive)
        {
            return false;
        }

        RegisterPortalSurface(provider, drawHandle);
        return true;
    }

    /// <summary>
    /// Removes any draw handle association owned by a retiring command without allocating.
    /// </summary>
    internal void ClearRetiredPortalSurfaceHandle(AdvancedGpuHandle retiredDraw)
    {
        if (!retiredDraw.IsValid)
            return;

        lock (_portalSurfaceSync)
        {
            for (int index = 0; index < _registeredPortalProviderCount; ++index)
                if (_registeredPortalDrawHandles[index] == retiredDraw)
                    _registeredPortalDrawHandles[index] = default;
        }
    }

    private void ClearPortalSurfaceRegistry()
    {
        lock (_portalSurfaceSync)
        {
            Array.Clear(_registeredPortalProviders, 0, _registeredPortalProviderCount);
            Array.Clear(_registeredPortalDrawHandles, 0, _registeredPortalProviderCount);
            _registeredPortalProviderCount = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindRegisteredPortalProviderIndexLocked(IAdvancedPortalSurfaceProvider provider)
    {
        for (int index = 0; index < _registeredPortalProviderCount; ++index)
            if (ReferenceEquals(_registeredPortalProviders[index], provider))
                return index;
        return -1;
    }
}
