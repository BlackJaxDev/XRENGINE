using System.Collections.Generic;
using XREngine.Components;

namespace XREngine.Editor.ComponentEditors;

public interface IXRComponentEditor
{
    void DrawInspector(XRComponent component, HashSet<object> visited);
}
