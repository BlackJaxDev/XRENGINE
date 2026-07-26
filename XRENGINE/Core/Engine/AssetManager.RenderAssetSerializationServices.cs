using XREngine.Rendering;

namespace XREngine;

public partial class AssetManager
{
    static AssetManager()
        => RenderAssetSerializationServices.Install(AssetManagerRenderAssetSerializationServices.Instance);
}
