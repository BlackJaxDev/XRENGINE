using System;

namespace XREngine.Components;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class XRComponentEditorAttribute : Attribute
{
    public XRComponentEditorAttribute(Type editorType)
    {
        if (editorType is null)
            throw new ArgumentNullException(nameof(editorType));

        EditorTypeName = editorType.AssemblyQualifiedName ?? editorType.FullName ?? editorType.Name;
    }

    public XRComponentEditorAttribute(string editorTypeName)
    {
        if (string.IsNullOrWhiteSpace(editorTypeName))
            throw new ArgumentException("Editor type name must be provided.", nameof(editorTypeName));

        EditorTypeName = editorTypeName;
    }

    public string EditorTypeName { get; }
}
