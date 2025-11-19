using System;

namespace XREngine.Scene.Transforms;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class XRTransformEditorAttribute : Attribute
{
    public XRTransformEditorAttribute(Type editorType)
    {
        ArgumentNullException.ThrowIfNull(editorType);
        EditorTypeName = editorType.AssemblyQualifiedName ?? editorType.FullName ?? editorType.Name;
    }

    public XRTransformEditorAttribute(string editorTypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editorTypeName);
        EditorTypeName = editorTypeName;
    }

    public string EditorTypeName { get; }
}
