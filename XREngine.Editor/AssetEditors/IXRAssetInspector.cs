using System.Collections.Generic;
using XREngine.Core.Files;

namespace XREngine.Editor.AssetEditors;

public interface IXRAssetInspector
{
    void DrawInspector(EditorImGuiUI.InspectorTargetSet targets, HashSet<object> visitedObjects);
}
