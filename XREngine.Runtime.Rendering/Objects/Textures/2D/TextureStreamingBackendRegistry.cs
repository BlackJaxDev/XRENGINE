namespace XREngine.Rendering;

/// <summary>
/// Static, AOT-safe registry populated by renderer leaf module entry points.
/// </summary>
internal static class TextureStreamingBackendRegistry
{
    private static readonly object Sync = new();
    private static ITextureStreamingBackendProvider? s_openGl;
    private static ITextureStreamingBackendProvider? s_vulkan;

    public static IDisposable Register(RuntimeGraphicsApiKind api, ITextureStreamingBackendProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (Sync)
        {
            ref ITextureStreamingBackendProvider? slot = ref GetSlot(api);
            if (slot is not null && !ReferenceEquals(slot, provider))
                throw new InvalidOperationException($"A texture-streaming provider is already registered for {api}.");

            slot = provider;
        }

        return new Registration(api, provider);
    }

    public static bool TryGet(RuntimeGraphicsApiKind api, out ITextureStreamingBackendProvider? provider)
    {
        provider = api switch
        {
            RuntimeGraphicsApiKind.OpenGL => Volatile.Read(ref s_openGl),
            RuntimeGraphicsApiKind.Vulkan => Volatile.Read(ref s_vulkan),
            _ => null,
        };
        return provider is not null;
    }

    public static ITextureStreamingBackendProvider GetRequired(RuntimeGraphicsApiKind api)
        => TryGet(api, out ITextureStreamingBackendProvider? provider) && provider is not null
            ? provider
            : throw new InvalidOperationException(
                $"The {api} texture-streaming provider has not been registered. Register the renderer leaf module at the composition root.");

    private static ref ITextureStreamingBackendProvider? GetSlot(RuntimeGraphicsApiKind api)
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
                ref ITextureStreamingBackendProvider? slot = ref GetSlot(api);
                if (ReferenceEquals(slot, currentProvider))
                    slot = null;
            }
        }
    }
}
