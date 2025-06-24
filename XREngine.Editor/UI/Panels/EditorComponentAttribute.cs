using System.Reflection;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>
/// Attribute to specify a custom editor for a class object.
/// </summary>
/// <param name="editorType"></param>
[AttributeUsage(AttributeTargets.Class)]
public abstract class EditorComponentAttribute : Attribute
{
    public abstract void CreateEditor(SceneNode node, PropertyInfo prop, object?[]? objects);
}
