namespace XREngine.Editor.UI;

/// <summary>Marks a virtual binding whose collection source is nested under an outer element path.</summary>
internal interface IEditorNestedListElementBinding : IEditorListElementBinding
{
    EditorNestedListSourceLease[] Paths { get; }
    EditorNestedListMutationContext MutationContext { get; }
    bool IsRowCurrent { get; }
}
