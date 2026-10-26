using System.Collections;
using XREngine.Core.Files;

namespace XREngine.Editor.UI;

/// <summary>Edits one level of collection-valued rows while retaining the real outer owner path.</summary>
internal static class EditorNestedListMutation
{
    public static bool Apply(EditorNestedListSourceLease[] paths, EEditorListMutation operation,
        int index, Func<object?, object?>? valueFactory, EditorNestedListMutationContext context,
        Func<bool> rowIsCurrent,
        IReadOnlyList<IEditorNestedListElementBinding>? expectedBindings = null)
    {
        if (paths.Length == 0 || !context.IsActive || !rowIsCurrent()) return false;
        if (paths.Any(path => !path.IsCommandCurrent()))
            throw new InvalidOperationException("The nested collection owner path changed.");

        object[] owners = paths.Select(path => path.Owner)
            .Distinct(ReferenceEqualityComparer.Instance).ToArray();
        IList[] lists = paths.Select(path => path.Inner)
            .Distinct<IList>(ReferenceEqualityComparer.Instance).ToArray();
        using IDisposable reservation = EditorListMutation.ReserveNestedMutation(owners, lists);
        if (!context.IsActive || !rowIsCurrent() || paths.Any(path => !path.IsCommandCurrent()))
            throw new InvalidOperationException("The nested collection row changed before the edit.");

        EditorNestedListMutationEntry[] entries = BuildEntries(paths, operation, index, valueFactory);
        if (entries.All(entry => EditorListSequenceKernel.SameSequence(entry.Before, entry.After)))
            return false;
        Validate(entries, command: true);
        IDisposable? interaction = null;
        Undo.ChangeScope? change = null;
        StructuralChangeHandle? hold = null;
        EditorNestedListMutationReceipt? receipt = null;
        bool committed = false;
        bool writesStarted = false;
        try
        {
            interaction = Undo.BeginUserInteraction();
            change = Undo.BeginChange("Edit nested list");
            receipt = new(entries, context);
            hold = Undo.BeginStructuralMutation("Edit nested list", receipt.Undo, receipt.Redo);
            if (!context.IsActive || !rowIsCurrent())
                throw new InvalidOperationException("The nested collection row changed before mutation.");
            Validate(entries, command: true);
            writesStarted = true;
            Write(entries, toApplied: true, command: true,
                viewCurrent: () => context.IsActive && rowIsCurrent());
            committed = true;
            try { receipt.AdvanceInitial(); }
            catch (Exception error) { Debug.LogException(error, "Could not advance committed nested-list rows."); }
            if (operation == EEditorListMutation.Set && expectedBindings is not null)
                foreach (IEditorNestedListElementBinding binding in expectedBindings)
                {
                    EditorNestedListMutationEntry? entry = entries.FirstOrDefault(candidate =>
                        ReferenceEquals(candidate.List, binding.Collection));
                    if (entry is not null && binding.Index >= 0 && binding.Index < entry.After.Length)
                        binding.AcceptCommitted(entry.After[binding.Index], entry.AfterState,
                            context.Rows.Revision);
                }
            return true;
        }
        catch
        {
            if (entries.All(entry => EditorListSequenceKernel.Matches(entry.List, entry.Before) &&
                ReferenceEquals(EditorListMutation.GetStateToken(entry.List), entry.BeforeState)))
                hold?.RemoveRestoredStep();
            throw;
        }
        finally
        {
            try
            {
                try { change?.Dispose(); }
                catch (Exception error) when (committed)
                { Debug.LogException(error, "Undo notification failed after the nested list edit committed."); }
            }
            finally
            {
                try { interaction?.Dispose(); }
                finally
                {
                    if (committed || writesStarted) MarkOwners(entries);
                    if (committed)
                    {
                        receipt?.PublishInitial();
                    }
                    hold?.Dispose();
                }
            }
        }
    }

    private static EditorNestedListMutationEntry[] BuildEntries(EditorNestedListSourceLease[] paths,
        EEditorListMutation operation, int index, Func<object?, object?>? valueFactory)
    {
        List<EditorNestedListMutationEntry> entries = [];
        foreach (IGrouping<IList, EditorNestedListSourceLease> group in paths.GroupBy(
            path => path.Inner, ReferenceEqualityComparer.Instance))
        {
            EditorNestedListSourceLease first = group.First();
            object?[] before = first.InnerDisplayed;
            object state = first.InnerState;
            if (group.Any(path => !ReferenceEquals(path.InnerState, state) ||
                !EditorListSequenceKernel.SameSequence(path.InnerDisplayed, before)))
                throw new InvalidOperationException("Shared nested list owners captured different source states.");
            if (!EditorListMutation.Supports(first.Inner, operation))
                throw new InvalidOperationException("This nested list does not support the requested edit.");
            int position = operation == EEditorListMutation.Insert && index < 0 ? before.Length : index;
            if (position < 0 || position > before.Length ||
                operation != EEditorListMutation.Insert && position >= before.Length)
                throw new InvalidOperationException("The nested list row index is stale.");
            object? next = operation == EEditorListMutation.Remove ? null :
                valueFactory?.Invoke(operation == EEditorListMutation.Set ? before[position] : null);
            Type valueType = ElementType(first.Inner);
            if (operation != EEditorListMutation.Remove && next is not null && !valueType.IsInstanceOfType(next) ||
                operation != EEditorListMutation.Remove && next is null && valueType.IsValueType &&
                Nullable.GetUnderlyingType(valueType) is null)
                throw new InvalidOperationException("The nested value does not match the list element type.");
            object?[] after = EditorListSequenceKernel.Stage(before, operation, position, next);
            entries.Add(new()
            {
                List = first.Inner,
                Paths = group.ToArray(),
                Before = before,
                After = after,
                BeforeState = state,
                AfterState = new object(),
                Operation = operation,
                Index = position
            });
        }
        return entries.ToArray();
    }

    internal static void Replay(EditorNestedListMutationEntry[] entries, bool toApplied)
    {
        object[] owners = entries.SelectMany(entry => entry.Paths).Select(path => path.Owner)
            .Distinct(ReferenceEqualityComparer.Instance).ToArray();
        IList[] lists = entries.Select(entry => entry.List).ToArray();
        using IDisposable reservation = EditorListMutation.ReserveNestedMutation(owners, lists);
        Validate(entries, command: false, toApplied);
        Write(entries, toApplied, command: false, viewCurrent: null);
        MarkOwners(entries);
    }

    private static void Validate(EditorNestedListMutationEntry[] entries, bool command, bool toApplied = true)
    {
        foreach (EditorNestedListMutationEntry entry in entries)
        {
            object?[] expected = toApplied ? entry.Before : entry.After;
            object token = toApplied ? entry.BeforeState : entry.AfterState;
            if (!entry.IsCurrent(command) || !EditorListSequenceKernel.Matches(entry.List, expected) ||
                !ReferenceEquals(EditorListMutation.GetStateToken(entry.List), token))
                throw new InvalidOperationException("The nested list source changed before the edit.");
        }
    }

    private static void Write(EditorNestedListMutationEntry[] entries, bool toApplied,
        bool command, Func<bool>? viewCurrent)
    {
        if (command && viewCurrent?.Invoke() != true)
            throw new InvalidOperationException("The nested list command lost its row lease.");
        Validate(entries, command, toApplied);
        int completed = 0;
        try
        {
            for (; completed < entries.Length; completed++)
            {
                EditorNestedListMutationEntry entry = entries[completed];
                EditorListSequenceKernel.Mutate(entry.List, entry.Operation, entry.Index,
                    entry.Before, entry.After, toApplied);
                EditorListMutation.AdvanceNestedState(entry.List,
                    toApplied ? entry.BeforeState : entry.AfterState,
                    toApplied ? entry.AfterState : entry.BeforeState);
                foreach (EditorNestedListSourceLease path in entry.Paths)
                    path.AcceptInner(toApplied ? entry.After : entry.Before,
                        toApplied ? entry.AfterState : entry.BeforeState);
            }
        }
        catch
        {
            for (int index = Math.Min(completed, entries.Length - 1); index >= 0; index--)
            {
                EditorNestedListMutationEntry entry = entries[index];
                object?[] target = toApplied ? entry.After : entry.Before;
                object?[] rollbackTarget = toApplied ? entry.Before : entry.After;
                if (entry.Paths.Any(path => !path.IsOwnerPathCurrent())) continue;
                object currentState = EditorListMutation.GetStateToken(entry.List);
                object preTransitionState = toApplied ? entry.BeforeState : entry.AfterState;
                object transitionedState = toApplied ? entry.AfterState : entry.BeforeState;
                object restoreState = preTransitionState;
                if (!ReferenceEquals(currentState, preTransitionState) &&
                    !ReferenceEquals(currentState, transitionedState))
                    continue;
                if (!EditorListSequenceKernel.Matches(entry.List, target)) continue;
                try
                {
                    EditorListSequenceKernel.Mutate(entry.List, entry.Operation, entry.Index,
                        entry.Before, entry.After, !toApplied);
                    if (ReferenceEquals(currentState, transitionedState))
                        EditorListMutation.RestoreNestedState(entry.List, currentState, restoreState);
                    if (!EditorListSequenceKernel.Matches(entry.List, rollbackTarget) ||
                        !ReferenceEquals(EditorListMutation.GetStateToken(entry.List), restoreState) ||
                        entry.Paths.Any(path => !path.IsOwnerPathCurrent()))
                        continue;
                    foreach (EditorNestedListSourceLease path in entry.Paths)
                        path.AcceptInner(rollbackTarget, restoreState);
                }
                catch { /* Retain observable partial state for explicit recovery. */ }
            }
            throw;
        }
    }

    private static void MarkOwners(EditorNestedListMutationEntry[] entries)
    {
        HashSet<XRAsset> marked = new(ReferenceEqualityComparer.Instance);
        foreach (EditorNestedListSourceLease path in entries.SelectMany(entry => entry.Paths))
            try
            {
                // History and earlier dirty observers can change a later owner.
                // Validate each path here and deduplicate only admitted documents.
                if (path.TryGetCurrentDocument(out XRAsset? document) && document is not null && marked.Add(document))
                    document.MarkDirty();
            }
            catch (Exception error) { Debug.LogException(error, "Could not mark the nested list owner dirty."); }
    }

    private static Type ElementType(IList list)
        => list.GetType().IsArray ? list.GetType().GetElementType()! :
            list.GetType() == typeof(ArrayList) ? typeof(object) : list.GetType().GetGenericArguments()[0];
}
