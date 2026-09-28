namespace XREngine;

public static partial class XRRuntimeEnvironment
{
    static XRRuntimeEnvironment()
        => AotRuntimeMetadataStore.MetadataArchiveReader = Core.Files.AssetArchiveReader.GetAsset;
}
