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

        // Native asset files can be referenced by stable ID and portable asset-root path.
        // Generated sidecars such as texture PNGs are not standalone XRAsset roots.
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

        if (RenderAssetSerializationServices.Current.TryCreatePortableAssetReference(
            asset.FilePath!,
            out string? portableReference))
        {
            // The path makes a fresh process/clone independent of ignored editor metadata,
            // while the ID still preserves reference identity across asset moves.
            emitter.Emit(new Scalar("Path"));
            emitter.Emit(new Scalar(portableReference));
        }

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
