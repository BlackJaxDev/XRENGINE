using XREngine.Rendering;

namespace XREngine.Editor.Publishing;

/// <summary>Captures device limits from values written into a cooked asset payload.</summary>
internal sealed class BrowserCookedDeviceRequirements
{
    internal const int CoreMaxTextureArrayLayers = 256;

    private int _maxTextureArrayLayers;

    /// <summary>Observe the value that the cooked serializer will write.</summary>
    internal object? Observe(object? value)
    {
        if (value is XRTexture2DArray textureArray)
            _maxTextureArrayLayers = Math.Max(_maxTextureArrayLayers, checked((int)textureArray.Depth));
        return value;
    }

    /// <summary>Return extra device limits only when the payload exceeds the WebGPU core minimum.</summary>
    internal IReadOnlyDictionary<string, int> GetRequiredLimits()
        => _maxTextureArrayLayers > CoreMaxTextureArrayLayers
            ? new SortedDictionary<string, int>(StringComparer.Ordinal)
            {
                ["maxTextureArrayLayers"] = _maxTextureArrayLayers,
            }
            : new SortedDictionary<string, int>(StringComparer.Ordinal);
}
