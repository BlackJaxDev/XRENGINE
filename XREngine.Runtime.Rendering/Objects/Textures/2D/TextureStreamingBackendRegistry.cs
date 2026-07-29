namespace XREngine.Rendering;

/// <summary>
/// Static, AOT-safe registry populated by renderer leaf module entry points.
/// </summary>
internal static class TextureStreamingBackendRegistry
{
    private static readonly object Sync = new();
    private static RegistrationEntry? s_openGl;
    private static RegistrationEntry? s_vulkan;

    public static IDisposable Register(RuntimeGraphicsApiKind api, ITextureStreamingBackendProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (Sync)
        {
            ref RegistrationEntry? slot = ref GetSlot(api);
            if (slot is not null)
            {
                if (!ReferenceEquals(slot.Provider, provider))
                {
                    throw new InvalidOperationException(
                        $"A texture-streaming provider is already registered for {api}.");
                }

                slot.LeaseCount++;
                return new Registration(api, provider);
            }

            slot = new RegistrationEntry(provider);
        }

        return new Registration(api, provider);
    }

    public static bool TryGet(RuntimeGraphicsApiKind api, out ITextureStreamingBackendProvider? provider)
    {
        RegistrationEntry? registration = api switch
        {
            RuntimeGraphicsApiKind.OpenGL => Volatile.Read(ref s_openGl),
            RuntimeGraphicsApiKind.Vulkan => Volatile.Read(ref s_vulkan),
            _ => null,
        };
        provider = registration?.Provider;
        return provider is not null;
    }

    public static ITextureStreamingBackendProvider GetRequired(RuntimeGraphicsApiKind api)
        => TryGet(api, out ITextureStreamingBackendProvider? provider) && provider is not null
            ? provider
            : throw new InvalidOperationException(
                $"The {api} texture-streaming provider has not been registered. Register the renderer leaf module at the composition root.");

    private static ref RegistrationEntry? GetSlot(RuntimeGraphicsApiKind api)
    {
        if (api == RuntimeGraphicsApiKind.OpenGL)
            return ref s_openGl;
        if (api == RuntimeGraphicsApiKind.Vulkan)
            return ref s_vulkan;

        throw new ArgumentOutOfRangeException(
            nameof(api),
            api,
            "Texture streaming is only provided by renderer leaf assemblies.");
    }

    private sealed class Registration(
        RuntimeGraphicsApiKind api,
        ITextureStreamingBackendProvider provider) : IDisposable
    {
        private ITextureStreamingBackendProvider? _provider = provider;

        public void Dispose()
        {
            ITextureStreamingBackendProvider? currentProvider =
                Interlocked.Exchange(ref _provider, null);
            if (currentProvider is null)
                return;

            lock (Sync)
            {
                ref RegistrationEntry? slot = ref GetSlot(api);
                if (slot is null || !ReferenceEquals(slot.Provider, currentProvider))
                    return;

                slot.LeaseCount--;
                if (slot.LeaseCount == 0)
                    slot = null;
            }
        }
    }

    private sealed class RegistrationEntry(ITextureStreamingBackendProvider provider)
    {
        public ITextureStreamingBackendProvider Provider { get; } = provider;
        public int LeaseCount { get; set; } = 1;
    }
}
