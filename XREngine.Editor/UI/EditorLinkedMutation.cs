using System.Runtime.CompilerServices;
using XREngine.Core.Files;

namespace XREngine.Editor.UI;

/// <summary>Applies a grouped edit to exact BCL linked lists.</summary>
internal static class EditorLinkedMutation
{
    private static readonly ConditionalWeakTable<object, EditorLinkedMutationState> States = new();
    private static readonly object BusyLock = new();
    private static readonly HashSet<object> BusyLists = new(ReferenceEqualityComparer.Instance);

    internal static object StateOf(object list) => States.GetValue(list, _ => new()).Token;

    internal static bool Supports(object? list, System.Reflection.PropertyInfo property)
        => EditorLinkedListAdapter.For(list) is not null &&
           EditorListSourceAccessor.For(property) is not null;

    internal static EditorLinkedSourceLease Capture(object owner, System.Reflection.PropertyInfo property,
        Func<bool> isViewCurrent)
    {
        if (owner is IEditorListElementBinding or IEditorLinkedElementBinding)
            throw new InvalidOperationException("Nested linked-list edits need a real source owner.");
        EditorListSourceAccessor accessor = EditorListSourceAccessor.For(property) ??
            throw new InvalidOperationException("Linked-list editing needs a nonvirtual auto-property source.");
        object list = property.GetValue(owner) ??
            throw new InvalidOperationException("Linked-list editing needs a non-null source.");
        EditorLinkedListAdapter adapter = EditorLinkedListAdapter.For(list) ??
            throw new InvalidOperationException("Only exact BCL linked lists with supported values can be edited.");
        object? world = EditorListMutation.WorldOf(owner);
        object? document = EditorListMutation.DocumentOf(owner);
        var node = EditorListMutation.NodeOf(owner);
        if (owner is global::XREngine.XRWorldObjectBase && document is null)
            throw new InvalidOperationException("The linked-list owner has no unique source document.");
        EditorLinkedSourceLease lease = new()
        {
            Owner = owner, Property = property, List = list, Adapter = adapter,
            Accessor = accessor, Displayed = adapter.Snapshot(list), State = StateOf(list),
            World = world, Document = document, SceneNode = node, IsViewCurrent = isViewCurrent
        };
        if (!lease.IsCurrent())
            throw new InvalidOperationException("The linked-list source changed during inspection.");
        return lease;
    }

    internal static void Set(IReadOnlyList<IEditorLinkedElementBinding> bindings,
        Func<object?, object?> valueFactory, Action? refresh)
    {
        if (bindings.Count == 0) return;
        EditorLinkedSourceLease[] sources = bindings.Select(binding => binding.Source).ToArray();
        object node = bindings[0].Node;
        int index = Array.IndexOf(sources[0].Displayed.Nodes, node);
        if (index < 0 || bindings.Any(binding =>
            Array.IndexOf(binding.Source.Displayed.Nodes, binding.Node) != index))
            throw new InvalidOperationException("The linked-list row changed.");
        Apply(sources, EEditorLinkedMutation.Set, index, valueFactory, refresh, bindings);
    }

    internal static void AddLast(EditorLinkedSourceLease[] sources, Action? refresh)
        => Apply(sources, EEditorLinkedMutation.AddLast, -1,
            _ => EditorListMutation.CreateDefaultElement(sources[0].Adapter.ElementType), refresh, null);

    internal static void Remove(EditorLinkedSourceLease[] sources, int index, Action? refresh)
        => Apply(sources, EEditorLinkedMutation.Remove, index, null, refresh, null);

    private static void Apply(EditorLinkedSourceLease[] sources, EEditorLinkedMutation operation,
        int index, Func<object?, object?>? valueFactory, Action? refresh,
        IReadOnlyList<IEditorLinkedElementBinding>? bindings)
    {
        if (sources.Length == 0) return;
        object[] owners = sources.Select(source => source.Owner)
            .Distinct<object>(ReferenceEqualityComparer.Instance).ToArray();
        EditorListMutation.Reserve(owners);
        List<object> reservedLists = [];
        EditorLinkedMutationEntry[] entries = [];
        IDisposable? interaction = null;
        Undo.ChangeScope? change = null;
        StructuralChangeHandle? hold = null;
        EditorLinkedMutationReceipt? receipt = null;
        bool writesStarted = false;
        bool committed = false;
        try
        {
            foreach (object list in sources.Select(source => source.List)
                .Distinct<object>(ReferenceEqualityComparer.Instance))
            {
                ReserveList(list);
                reservedLists.Add(list);
            }
            // Capture every target before a value factory can call user code.
            foreach (EditorLinkedSourceLease source in sources)
                if (!source.IsCurrent())
                    throw new InvalidOperationException("The linked-list command source changed.");
            ValidateBindings(bindings);
            List<EditorLinkedMutationEntry> prepared = [];
            foreach (IGrouping<object, EditorLinkedSourceLease> group in sources.GroupBy(
                source => source.List, ReferenceEqualityComparer.Instance))
            {
                EditorLinkedSourceLease first = group.First();
                EditorLinkedListSnapshot before = first.Displayed;
                int position = operation == EEditorLinkedMutation.AddLast ? before.Nodes.Length : index;
                if (position < 0 || position > before.Nodes.Length ||
                    operation != EEditorLinkedMutation.AddLast && position == before.Nodes.Length)
                    throw new InvalidOperationException("The linked-list row is stale.");
                object node = operation == EEditorLinkedMutation.AddLast
                    ? first.Adapter.CreateNode(EditorListMutation.CreateDefaultElement(first.Adapter.ElementType))
                    : before.Nodes[position];
                prepared.Add(new EditorLinkedMutationEntry
                {
                    List = first.List, Adapter = first.Adapter,
                    Owners = group.Select(source => new EditorLinkedOwnerProof(source)).ToArray(),
                    Before = before, After = before, BeforeState = first.State,
                    AfterState = new object(), Operation = operation, Index = position, Node = node
                });
            }
            // A callback may change another target. Do not adopt its new state.
            for (int slot = 0; slot < prepared.Count; slot++)
            {
                EditorLinkedMutationEntry entry = prepared[slot];
                object? next = operation == EEditorLinkedMutation.Set
                    ? valueFactory?.Invoke(entry.Before.Values[entry.Index]) :
                    operation == EEditorLinkedMutation.AddLast ? entry.Adapter.Value(entry.Node) : null;
                if (operation != EEditorLinkedMutation.Remove && next is not null &&
                    !entry.Adapter.ElementType.IsInstanceOfType(next) ||
                    operation != EEditorLinkedMutation.Remove && next is null &&
                    entry.Adapter.ElementType.IsValueType &&
                    Nullable.GetUnderlyingType(entry.Adapter.ElementType) is null)
                    throw new InvalidOperationException("The value does not match the linked-list element type.");
                object[] afterNodes = operation == EEditorLinkedMutation.AddLast
                    ? [.. entry.Before.Nodes, entry.Node] :
                    operation == EEditorLinkedMutation.Remove
                        ? entry.Before.Nodes.Where((_, i) => i != entry.Index).ToArray()
                        : [.. entry.Before.Nodes];
                object?[] afterValues = operation == EEditorLinkedMutation.AddLast
                    ? [.. entry.Before.Values, next] :
                    operation == EEditorLinkedMutation.Remove
                        ? entry.Before.Values.Where((_, i) => i != entry.Index).ToArray()
                        : [.. entry.Before.Values];
                if (operation == EEditorLinkedMutation.Set) afterValues[entry.Index] = next;
                if (operation == EEditorLinkedMutation.AddLast &&
                    !EditorListMutation.SameValue(entry.Adapter.Value(entry.Node), next))
                    entry.Adapter.Set(entry.Node, next);
                // Entry is immutable after staging; keep a replacement with the new sequence.
                prepared[slot] = new EditorLinkedMutationEntry
                {
                    List = entry.List, Adapter = entry.Adapter, Owners = entry.Owners,
                    Before = entry.Before, After = new(afterNodes, afterValues),
                    BeforeState = entry.BeforeState, AfterState = entry.AfterState,
                    Operation = entry.Operation, Index = entry.Index, Node = entry.Node
                };
            }
            entries = [.. prepared];
            if (operation == EEditorLinkedMutation.Set && entries.All(entry =>
                EditorListMutation.SameValue(entry.Before.Values[entry.Index],
                    entry.After.Values[entry.Index])))
                return;
            Preflight(entries, true);
            ValidateFrozen(entries, true);
            ValidateBindings(bindings);
            interaction = Undo.BeginUserInteraction();
            change = Undo.BeginChange("Edit linked list");
            receipt = new(entries, refresh);
            hold = Undo.BeginStructuralMutation("Edit linked list", receipt.Undo, receipt.Redo);
            try
            {
                ValidateFrozen(entries, true);
                ValidateBindings(bindings);
                ValidateFrozen(entries, true);
                writesStarted = true;
                Write(entries, true);
                committed = true;
            }
            catch
            {
                if (entries.All(entry => Matches(entry, true))) hold.RemoveRestoredStep();
                throw;
            }
        }
        finally
        {
            try
            {
                if (committed && bindings is not null)
                    foreach (IEditorLinkedElementBinding binding in bindings)
                        try
                        {
                            EditorLinkedMutationEntry? entry = entries.FirstOrDefault(candidate =>
                                ReferenceEquals(candidate.List, binding.Source.List));
                            if (entry is not null)
                            {
                                binding.Source.AcceptCommitted(entry.After, entry.AfterState);
                                binding.AcceptCommitted(entry.After.Values[entry.Index], entry.AfterState);
                            }
                        }
                        catch (Exception error)
                        { Debug.LogException(error, "Could not refresh a committed linked-list binding."); }
                try
                {
                    try
                    {
                        try { change?.Dispose(); }
                        catch (Exception error) when (committed)
                        { Debug.LogException(error, "Undo notification failed after the linked-list edit committed."); }
                    }
                    finally
                    {
                        try { interaction?.Dispose(); }
                        catch (Exception error) when (committed)
                        { Debug.LogException(error, "Undo interaction notification failed after the linked-list edit committed."); }
                    }
                }
                finally
                {
                    try
                    {
                        if (committed && operation != EEditorLinkedMutation.Set)
                            refresh?.Invoke();
                    }
                    catch (Exception error) { Debug.LogException(error, "Could not refresh linked-list rows."); }
                    finally
                    {
                        if (committed || writesStarted) MarkOwners(entries);
                    }
                }
            }
            finally
            {
                ReleaseLists(reservedLists);
                EditorListMutation.Release(owners);
                hold?.Dispose();
            }
        }
    }

    internal static void Replay(EditorLinkedMutationEntry[] entries, bool toApplied)
    {
        object[] owners = entries.SelectMany(entry => entry.Owners).Select(source => source.Owner)
            .Distinct<object>(ReferenceEqualityComparer.Instance).ToArray();
        EditorListMutation.Reserve(owners);
        List<object> reservedLists = [];
        bool committed = false;
        bool writesStarted = false;
        try
        {
            foreach (object list in entries.Select(entry => entry.List)
                .Distinct<object>(ReferenceEqualityComparer.Instance))
            {
                ReserveList(list);
                reservedLists.Add(list);
            }
            Preflight(entries, toApplied);
            ValidateFrozen(entries, toApplied);
            writesStarted = true;
            Write(entries, toApplied);
            committed = true;
        }
        finally
        {
            ReleaseLists(reservedLists);
            EditorListMutation.Release(owners);
            if (!committed && writesStarted) MarkOwners(entries);
        }
    }

    private static void Preflight(EditorLinkedMutationEntry[] entries, bool toApplied)
    {
        foreach (EditorLinkedMutationEntry entry in entries)
            if (!entry.IsOwnerCurrent())
                throw new InvalidOperationException("Undo was blocked because a linked-list owner changed.");
        ValidateFrozen(entries, toApplied);
    }

    private static void ValidateBindings(IReadOnlyList<IEditorLinkedElementBinding>? bindings)
    {
        if (bindings is null) return;
        foreach (IEditorLinkedElementBinding binding in bindings)
        {
            EditorLinkedSourceLease source = binding.Source;
            int index = Array.IndexOf(source.Displayed.Nodes, binding.Node);
            if (!binding.IsCurrent || index < 0 ||
                !EditorListMutation.SameValue(binding.DisplayedValue,
                    source.Displayed.Values[index]) ||
                !ReferenceEquals(source.Adapter.OwningList(binding.Node), source.List))
                throw new InvalidOperationException("The linked-list row changed while it was visible.");
        }
    }

    private static void ValidateFrozen(EditorLinkedMutationEntry[] entries, bool toApplied)
    {
        foreach (EditorLinkedMutationEntry entry in entries)
            if (!entry.IsOwnerLocationCurrent() || !Matches(entry, toApplied))
                throw new InvalidOperationException("The linked list changed before the edit could be written.");
    }

    private static bool Matches(EditorLinkedMutationEntry entry, bool before)
    {
        EditorLinkedListSnapshot expected = before ? entry.Before : entry.After;
        object token = before ? entry.BeforeState : entry.AfterState;
        if (!ReferenceEquals(StateOf(entry.List), token) ||
            !entry.Adapter.Matches(entry.List, expected)) return false;
        if (entry.Operation == EEditorLinkedMutation.AddLast && before ||
            entry.Operation == EEditorLinkedMutation.Remove && !before)
            return entry.Adapter.OwningList(entry.Node) is null &&
                   EditorListMutation.SameValue(entry.Adapter.Value(entry.Node),
                       entry.Operation == EEditorLinkedMutation.AddLast
                           ? entry.After.Values[entry.Index] : entry.Before.Values[entry.Index]);
        return true;
    }

    private static void Write(EditorLinkedMutationEntry[] entries, bool toApplied)
    {
        ValidateFrozen(entries, toApplied);
        int completed = 0;
        try
        {
            for (; completed < entries.Length; completed++)
            {
                Mutate(entries[completed], toApplied);
                States.GetValue(entries[completed].List, _ => new()).Token = toApplied
                    ? entries[completed].AfterState : entries[completed].BeforeState;
                if (!entries[completed].Adapter.Matches(entries[completed].List,
                    toApplied ? entries[completed].After : entries[completed].Before))
                    throw new InvalidOperationException("The linked list changed during the edit.");
            }
        }
        catch (Exception error)
        {
            for (int i = completed; i >= 0; i--)
            {
                if (i >= entries.Length) continue;
                EditorLinkedMutationEntry entry = entries[i];
                if (!Matches(entry, !toApplied)) continue;
                try
                {
                    Mutate(entry, !toApplied);
                    States.GetValue(entry.List, _ => new()).Token = toApplied
                        ? entry.BeforeState : entry.AfterState;
                }
                catch { /* Retain an observed conflict for manual recovery. */ }
            }
            if (!entries.All(entry => Matches(entry, toApplied)))
                throw new InvalidOperationException(
                    "The linked-list edit has a partial conflict and needs manual recovery.", error);
            throw;
        }
    }

    private static void Mutate(EditorLinkedMutationEntry entry, bool toApplied)
    {
        switch (entry.Operation)
        {
            case EEditorLinkedMutation.Set:
                entry.Adapter.Set(entry.Node, toApplied
                    ? entry.After.Values[entry.Index] : entry.Before.Values[entry.Index]);
                break;
            case EEditorLinkedMutation.AddLast:
                if (toApplied) entry.Adapter.Append(entry.List, entry.Node);
                else entry.Adapter.Remove(entry.List, entry.Node);
                break;
            case EEditorLinkedMutation.Remove:
                if (toApplied) entry.Adapter.Remove(entry.List, entry.Node);
                else entry.Adapter.Insert(entry.List,
                    entry.Index == 0 ? null : entry.Before.Nodes[entry.Index - 1], entry.Node);
                break;
        }
    }

    private static void ReserveList(object list)
    {
        lock (BusyLock)
            if (!BusyLists.Add(list))
                throw new InvalidOperationException("A linked-list edit is already active for this collection.");
    }

    private static void ReleaseLists(List<object> lists)
    {
        lock (BusyLock)
            foreach (object list in lists) BusyLists.Remove(list);
    }

    internal static void MarkOwners(EditorLinkedMutationEntry[] entries)
    {
        foreach (XRAsset document in entries.SelectMany(entry => entry.Owners)
            .Select(source => source.Document).OfType<XRAsset>()
            .Distinct<XRAsset>(ReferenceEqualityComparer.Instance))
            try { document.MarkDirty(); }
            catch (Exception error) { Debug.LogException(error, "Could not mark an edited linked-list owner dirty."); }
    }
}
