using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

/// <summary>Coordinates shared image settings across additive authored material schemas without extending texture payloads.</summary>
internal static class AuthoredTextureSettingsRestoreRegistry
{
    internal static object Gate { get; } = new();
    private static readonly ConditionalWeakTable<XRTexture2D, RestoredSettings> RestoredImages = new();

    internal static void Check(XRTexture2D image, PublishedStandardLitTextureSettings settings,
        string diagnostic = "AuthoredTextured.TextureSettingsConflict")
    {
        if (RestoredImages.TryGetValue(image, out RestoredSettings? previous) && previous.Value != settings &&
            PublishedStandardLitTextureSettings.Capture(image) != settings)
            throw new InvalidDataException($"{diagnostic}: a shared image already carries different authored settings.");
    }

    internal static void Restore(XRTexture2D image, PublishedStandardLitTextureSettings settings)
    {
        settings.ApplyTo(image);
        Remember(image, settings);
    }

    internal static void Remember(XRTexture2D image, PublishedStandardLitTextureSettings settings)
    {
        RestoredImages.Remove(image);
        RestoredImages.Add(image, new(settings));
    }

    private sealed class RestoredSettings(PublishedStandardLitTextureSettings value)
    {
        internal PublishedStandardLitTextureSettings Value { get; } = value;
    }
}
