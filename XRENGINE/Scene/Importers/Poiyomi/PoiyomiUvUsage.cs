namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// Finds authored texture UV-channel requests and validates them against a mesh.
/// </summary>
internal static class PoiyomiUvUsage
{
    public static int[] GetRequestedChannels(UnityMaterialDocument document)
    {
        HashSet<int> channels = [];
        foreach (string textureProperty in document.Textures.Keys)
        {
            string selector = string.Equals(textureProperty, "_ToonRamp", StringComparison.Ordinal)
                ? "_ToonRampUVSelector"
                : textureProperty + "UV";

            if (document.TryGetInt(selector, out int channel) && channel is >= 0 and <= 3)
                channels.Add(channel);
        }

        return [.. channels.Order()];
    }

    public static MaterialConversionDiagnostic[] Validate(UnityMaterialDocument document, uint availableChannelCount)
    {
        List<MaterialConversionDiagnostic> diagnostics = [];
        foreach (int requestedChannel in GetRequestedChannels(document))
        {
            if (requestedChannel < availableChannelCount)
                continue;

            diagnostics.Add(new MaterialConversionDiagnostic(
                MaterialConversionDiagnosticCodes.RequestedUvChannelMissing,
                MaterialConversionDiagnosticSeverity.Warning,
                $"The material requests UV{requestedChannel}, but the mesh exposes {availableChannelCount} UV channel(s). The uber vertex path falls back to UV0, or (0,0) when UV0 is absent.",
                $"UV{requestedChannel}"));
        }

        return [.. diagnostics];
    }
}
