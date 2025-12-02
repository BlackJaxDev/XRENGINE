using System.Collections.Generic;
using XREngine.Core.Files;

namespace XREngine.Editor.AssetEditors;

public interface IXRAssetInspector
{
    void DrawInspector(XRAsset asset, HashSet<object> visitedObjects);
}
