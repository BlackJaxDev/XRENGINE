namespace XREngine.Editor.UI;

/// <summary>Marks a virtual binding whose collection source is nested under an outer element path.</summary>
internal interface IEditorNestedListElementBinding : IEditorListElementBinding
{
    EditorNestedListSourceLease[] Paths { get; }
    /// <summary>The path of the inner list that holds this element for this binding's target.</summary>
    EditorNestedListSourceLease SourcePath { get; }
    EditorNestedListMutationContext MutationContext { get; }
    bool IsRowCurrent { get; }
}
