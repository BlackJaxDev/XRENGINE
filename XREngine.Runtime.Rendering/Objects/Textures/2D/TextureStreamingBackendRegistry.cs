namespace XREngine.Rendering;

/// <summary>
/// Static, AOT-safe registry populated by renderer leaf module entry points.
/// </summary>
internal static class TextureStreamingBackendRegistry
{
    private static ITextureStreamingBackendProvider? s_openGl;
    private static ITextureStreamingBackendProvider? s_vulkan;

    public static void Register(RuntimeGraphicsApiKind api, ITextureStreamingBackendProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        switch (api)
        {
            case RuntimeGraphicsApiKind.OpenGL:
                Volatile.Write(ref s_openGl, provider);
                break;
            case RuntimeGraphicsApiKind.Vulkan:
                Volatile.Write(ref s_vulkan, provider);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(api), api, "Texture streaming is only provided by renderer leaf assemblies.");
        }
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
}
