using System.Collections.Generic;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.TransformEditors;

public interface IXRTransformEditor
{
    void DrawInspector(TransformBase transform, HashSet<object> visited);
}
