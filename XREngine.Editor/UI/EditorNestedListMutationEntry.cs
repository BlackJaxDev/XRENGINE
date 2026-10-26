using System.Collections;

namespace XREngine.Editor.UI;

/// <summary>Stores the exact nested list state plus every outer owner path needed for safe replay.</summary>
internal sealed class EditorNestedListMutationEntry
{
    public required IList List { get; init; }
    public required EditorNestedListSourceLease[] Paths { get; init; }
    public required object?[] Before { get; init; }
    public required object?[] After { get; init; }
    public required object BeforeState { get; init; }
    public required object AfterState { get; init; }
    public required EEditorListMutation Operation { get; init; }
    public required int Index { get; init; }

    public bool IsCurrent(bool command)
        => Paths.Length != 0 && Paths.All(path =>
            (command ? path.IsCommandCurrent() : path.IsOwnerPathCurrent()) && path.IsInnerCurrent()) &&
           Paths.All(path => ReferenceEquals(path.Inner, List));
}
