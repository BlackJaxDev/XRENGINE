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
        => ShouldWriteReference(asset, AssetSerializationServices.Current);

    private static bool ShouldWriteReference(XRAsset asset, IAssetSerializationServices services)
    {
        ArgumentNullException.ThrowIfNull(asset);

        // Generic converters and graph visitors must retain a text converter's inline decision.
        if (asset is TextFile textFile && TextFileYamlTypeConverter.IsWritingInline(textFile))
            return false;

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

        return services.AssetPathExists(asset.FilePath);
    }

    public static void WriteReference(IEmitter emitter, XRAsset asset)
        => WriteReference(emitter, asset, AssetSerializationServices.Current);

    private static void WriteReference(IEmitter emitter, XRAsset asset, IAssetSerializationServices services)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        ArgumentNullException.ThrowIfNull(asset);

        emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
        emitter.Emit(new Scalar("ID"));
        emitter.Emit(new Scalar(asset.ID.ToString()));

        if (services.TryCreatePortableAssetReference(
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
        => TryEmitReference(emitter, asset, AssetSerializationServices.Current);

    internal static bool TryEmitReference(IEmitter emitter, XRAsset? asset, IAssetSerializationServices services)
    {
        if (asset is null || !ShouldWriteReference(asset, services))
            return false;

        WriteReference(emitter, asset, services);
        return true;
    }
}
