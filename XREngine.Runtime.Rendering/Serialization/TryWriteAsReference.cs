using XREngine.Core.Files;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace XREngine;

/// <summary>
/// Emits compact references for nested, externalized engine assets.
/// </summary>
public static class TryWriteAsReference
{
    private const string NativeAssetExtension = ".asset";

    public static bool ShouldWriteReference(XRAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        // CurrentDepth is 0 before the first MappingStart, so depth 0 means root object.
        if (DepthTrackingEventEmitter.CurrentDepth < 1)
            return false;

        // Embedded assets (SourceAsset != self) must serialize inline.
        if (!ReferenceEquals(asset.SourceAsset, asset))
            return false;

        if (string.IsNullOrWhiteSpace(asset.FilePath))
            return false;

        // A compact reference must resolve in a later process. Native .asset files
        // carry IDs in metadata; generated sidecars such as texture PNGs do not.
        if (!string.Equals(
                Path.GetExtension(asset.FilePath),
                NativeAssetExtension,
                StringComparison.OrdinalIgnoreCase))
            return false;

        return File.Exists(asset.FilePath);
    }

    public static void WriteReference(IEmitter emitter, XRAsset asset)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        ArgumentNullException.ThrowIfNull(asset);

        emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
        emitter.Emit(new Scalar("ID"));
        emitter.Emit(new Scalar(asset.ID.ToString()));
        emitter.Emit(new MappingEnd());
    }

    public static bool TryEmitReference(IEmitter emitter, XRAsset? asset)
    {
        if (asset is null || !ShouldWriteReference(asset))
            return false;

        WriteReference(emitter, asset);
        return true;
    }
}
