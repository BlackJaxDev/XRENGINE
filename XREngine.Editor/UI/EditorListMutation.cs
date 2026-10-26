using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene;
using static XREngine.Editor.UI.EditorListSequenceKernel;

namespace XREngine.Editor.UI;

/// <summary>Edits exact BCL lists with one owner-scoped Undo step.</summary>
internal static class EditorListMutation
{
    private static readonly ConditionalWeakTable<IList, EditorListMutationState> States = new();
    private static readonly HashSet<object> BusyOwners = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<IList> BusyLists = new(ReferenceEqualityComparer.Instance);
    private static readonly object BusyLock = new();

    public static void Set(IReadOnlyList<IEditorListElementBinding> bindings,
        Func<object?, object?> update)
    {
        if (bindings.Count == 0) return;
        IEditorListElementBinding first = ValidateRow(bindings);
        Apply(bindings.Select(binding => (binding.Owner, binding.OwnerProperty)).ToArray(),
            EEditorListMutation.Set, first.Index, update, first.Context, bindings);
    }

    public static void Remove(IReadOnlyList<IEditorListElementBinding> bindings)
    {
        if (bindings.Count == 0) return;
        IEditorListElementBinding first = ValidateRow(bindings);
        Apply(bindings.Select(binding => (binding.Owner, binding.OwnerProperty)).ToArray(),
            EEditorListMutation.Remove, first.Index, null, first.Context, bindings);
    }

    private static IEditorListElementBinding ValidateRow(IReadOnlyList<IEditorListElementBinding> bindings)
    {
        IEditorListElementBinding first = bindings[0];
        ulong expectedRevision = first.Context.Revision;
        if (!first.IsLeased || !first.Context.IsActive || first.Index < 0 || first.Index >= first.Context.Rows.Count ||
            first.Context.Rows.GetItemId(first.Index) != first.RowId)
            throw new InvalidOperationException("The list row is no longer active.");
        for (int i = 0; i < bindings.Count; i++)
            if (!ReferenceEquals(bindings[i].Context, first.Context) ||
                !bindings[i].IsLeased ||
                bindings[i].ContextRevision != first.Context.Revision ||
                bindings[i].Index != first.Index || bindings[i].RowId != first.RowId ||
                !Equals(bindings[i].OwnerProperty, first.OwnerProperty))
                throw new InvalidOperationException("The list row is no longer current.");
        if (first.Context.Revision != expectedRevision)
            throw new InvalidOperationException("The list row changed during validation.");
        return first;
    }

    public static void Apply(IReadOnlyList<(object Owner, PropertyInfo Property)> targets,
        EEditorListMutation operation, int index, Func<object?, object?>? valueFactory,
        EditorListMutationContext? context = null,
        IReadOnlyList<IEditorListElementBinding>? expectedBindings = null,
        IReadOnlyList<EditorListSourceLease>? expectedSources = null)
    {
        if (targets.Count == 0)
            return;
        if (targets.Any(target => target.Owner is IEditorListElementBinding))
            throw new InvalidOperationException("Nested list edits need a real source owner.");
        object[] owners = targets.Select(target => target.Owner).Distinct(ReferenceEqualityComparer.Instance).ToArray();
        Reserve(owners);
        List<IList> reservedLists = [];
        ulong expectedContextRevision = context?.Revision ?? 0;
        EditorListMutationEntry[] entries = [];
        EditorListMutationReceipt? receipt = null;
        bool committed = false;
        bool writesStarted = false;
        IDisposable? interaction = null;
        Undo.ChangeScope? change = null;
        StructuralChangeHandle? hold = null;
        try
        {
            if (context is { IsActive: false })
                throw new InvalidOperationException("The list detail view was closed.");
            if (expectedBindings is not null)
                foreach (IList expectedList in expectedBindings.Select(binding => binding.Collection)
                    .Distinct<IList>(ReferenceEqualityComparer.Instance))
                {
                    ReserveList(expectedList);
                    reservedLists.Add(expectedList);
                }
            if (expectedSources is not null)
                foreach (IList expectedList in expectedSources.Select(source => source.List)
                    .Distinct<IList>(ReferenceEqualityComparer.Instance))
                    if (!reservedLists.Any(reserved => ReferenceEquals(reserved, expectedList)))
                    {
                        ReserveList(expectedList);
                        reservedLists.Add(expectedList);
                    }
            List<(object Owner, PropertyInfo Property, IList List,
                object? World, object? Document, XREngine.Scene.SceneNode? Node,
                EditorListSourceAccessor Accessor, object?[] Before, object BeforeState)> resolved = [];
            for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                (object owner, PropertyInfo property) = targets[targetIndex];
                EditorListSourceAccessor accessor = EditorListSourceAccessor.For(property) ??
                    throw new InvalidOperationException(
                        "This list property has a callback-bearing source and is read-only in the guarded editor.");
                object? world = WorldOf(owner);
                object? document = DocumentOf(owner);
                XREngine.Scene.SceneNode? node = NodeOf(owner);
                if (owner is global::XREngine.XRWorldObjectBase && document is null)
                    throw new InvalidOperationException("The scene owner has no unique writable source document.");
                if (property.GetValue(owner) is not IList list || !Supports(list, operation))
                    throw new InvalidOperationException("This collection type does not support guarded list edits.");
                if (!accessor.IsCurrent(owner, list))
                    throw new InvalidOperationException("The list property's backing source does not match its value.");
                if (!ReferenceEquals(world, WorldOf(owner)) ||
                    !ReferenceEquals(document, DocumentOf(owner)) ||
                    !ReferenceEquals(node, NodeOf(owner)))
                    throw new InvalidOperationException("The list owner moved during source resolution.");
                if (expectedBindings is not null &&
                    (targetIndex >= expectedBindings.Count ||
                     !ReferenceEquals(list, expectedBindings[targetIndex].Collection) ||
                     !ReferenceEquals(GetStateToken(list), expectedBindings[targetIndex].CollectionState) ||
                     !ReferenceEquals(DocumentOf(owner), expectedBindings[targetIndex].OwnerDocument) ||
                     !ReferenceEquals(WorldOf(owner), expectedBindings[targetIndex].OwnerWorld) ||
                     !ReferenceEquals(NodeOf(owner), expectedBindings[targetIndex].OwnerNode) ||
                     expectedBindings[targetIndex].ContextRevision != expectedContextRevision ||
                     expectedBindings[targetIndex].Index < 0 ||
                     expectedBindings[targetIndex].Index >= list.Count ||
                     !SameValue(list[expectedBindings[targetIndex].Index],
                         expectedBindings[targetIndex].DisplayedValue)))
                    throw new InvalidOperationException("The list row source was replaced.");
                if (expectedSources is not null &&
                    (targetIndex >= expectedSources.Count ||
                     !ReferenceEquals(expectedSources[targetIndex].Owner, owner) ||
                     !Equals(expectedSources[targetIndex].Property, property) ||
                     !ReferenceEquals(expectedSources[targetIndex].List, list) ||
                     !ReferenceEquals(expectedSources[targetIndex].State, GetStateToken(list)) ||
                     expectedSources[targetIndex].Count != list.Count ||
                     !ReferenceEquals(expectedSources[targetIndex].World, world) ||
                     !ReferenceEquals(expectedSources[targetIndex].Document, document) ||
                     !ReferenceEquals(expectedSources[targetIndex].Node, node)))
                    throw new InvalidOperationException("The list command source changed while it was visible.");
                if (!reservedLists.Any(reserved => ReferenceEquals(reserved, list)))
                {
                    ReserveList(list);
                    reservedLists.Add(list);
                }
                // Freeze this source before another owner's getter can change it.
                object?[] sourceBefore = Snapshot(list);
                object sourceState = GetStateToken(list);
                resolved.Add((owner, property, list, world, document, node, accessor,
                    sourceBefore, sourceState));
            }
            List<EditorListMutationEntry> prepared = [];
            foreach ((object owner, PropertyInfo property, IList list,
                object? world, object? document, XREngine.Scene.SceneNode? node,
                EditorListSourceAccessor accessor, object?[] before, object beforeState) in resolved)
            {
                EditorListMutationEntry? existing = prepared.FirstOrDefault(entry => ReferenceEquals(entry.List, list));
                if (existing is not null)
                {
                    if (!Equals(existing.Property, property) ||
                        index >= 0 && existing.Index != index)
                        throw new InvalidOperationException("Shared list targets request different edits.");
                    object[] sharedOwners = [.. existing.Owners, owner];
                    object?[] sharedWorlds = [.. existing.OwnerWorlds, world];
                    object?[] sharedDocuments = [.. existing.OwnerDocuments, document];
                    XREngine.Scene.SceneNode?[] sharedNodes = [.. existing.OwnerNodes, node];
                    prepared[prepared.IndexOf(existing)] = CopyWithOwners(existing, sharedOwners,
                        sharedWorlds, sharedDocuments, sharedNodes);
                    continue;
                }
                int position = index < 0 && operation == EEditorListMutation.Insert ? before.Length : index;
                if (position < 0 || position > before.Length ||
                    operation != EEditorListMutation.Insert && position == before.Length)
                    throw new InvalidOperationException("The list row index is stale.");
                prepared.Add(new EditorListMutationEntry
                {
                    List = list, Property = property, Owners = [owner], OwnerWorlds = [world],
                    SourceAccessor = accessor,
                    OwnerDocuments = [document], OwnerNodes = [node],
                    Before = before, After = before, BeforeState = beforeState,
                    AfterState = new object(), Operation = operation, Index = position
                });
            }
            foreach (EditorListMutationEntry entry in prepared)
            {
                object? next = operation == EEditorListMutation.Remove ? null :
                    ConvertElement(valueFactory?.Invoke(operation == EEditorListMutation.Set
                        ? entry.Before[entry.Index] : null), ElementType(entry.List));
                entry.After = Stage(entry.Before, operation, entry.Index, next);
            }
            entries = [.. prepared];
            if (entries.All(entry => SameSequence(entry.Before, entry.After)))
                return;
            foreach (EditorListMutationEntry entry in entries)
            {
                if (context is not null && (!context.IsActive || context.Revision != expectedContextRevision) ||
                    !entry.IsOwnerCurrent() || !Matches(entry.List, entry.Before) ||
                    !ReferenceEquals(States.GetValue(entry.List, _ => new()).Token, entry.BeforeState))
                    throw new InvalidOperationException("The list changed while the edit was prepared.");
            }
            Preflight(entries, toApplied: true);
            ValidateFrozenSources(entries, toApplied: true);
            interaction = Undo.BeginUserInteraction();
            change = Undo.BeginChange("Edit list");
            receipt = new(entries, context);
            hold = Undo.BeginStructuralMutation("Edit list", receipt.Undo, receipt.Redo);
            try
            {
                ValidateViewSources(context, expectedContextRevision, expectedBindings, expectedSources);
                writesStarted = true;
                WritePrepared(entries, toApplied: true);
                committed = true;
            }
            catch
            {
                if (entries.All(entry => Matches(entry.List, entry.Before) &&
                    ReferenceEquals(States.GetValue(entry.List, _ => new()).Token, entry.BeforeState)))
                    hold.RemoveRestoredStep();
                throw;
            }
        }
        finally
        {
            try
            {
                if (committed)
                {
                    try { receipt!.AdvanceInitial(); }
                    catch (Exception error) { Debug.LogException(error, "Could not advance committed list rows."); }
                    if (operation == EEditorListMutation.Set && expectedBindings is not null && context is not null)
                    {
                        try
                        {
                            foreach (IEditorListElementBinding binding in expectedBindings)
                            {
                                EditorListMutationEntry? entry = entries.FirstOrDefault(candidate =>
                                    ReferenceEquals(candidate.List, binding.Collection));
                                if (entry is not null)
                                    binding.AcceptCommitted(entry.After[binding.Index], entry.AfterState,
                                        context.Revision);
                            }
                        }
                        catch (Exception error) { Debug.LogException(error, "Could not refresh committed list bindings."); }
                    }
                }
                try
                {
                    try
                    {
                        if (change is not null)
                        {
                            try { change.Dispose(); }
                            catch (Exception error) when (committed)
                            { Debug.LogException(error, "Undo notification failed after the list edit committed."); }
                        }
                    }
                    finally { interaction?.Dispose(); }
                }
                finally
                {
                    if (committed)
                    {
                        try { receipt!.PublishInitial(); }
                        catch (Exception error) { Debug.LogException(error, "Could not publish committed list rows."); }
                        finally { MarkOwners(entries); }
                    }
                    else if (writesStarted)
                        MarkOwners(entries);
                }
            }
            finally
            {
                try
                {
                    ReleaseLists(reservedLists);
                    Release(owners);
                }
                finally { hold?.Dispose(); }
            }
        }
    }

    internal static void Replay(EditorListMutationEntry[] entries, bool toApplied)
    {
        object[] owners = entries.SelectMany(entry => entry.Owners)
            .Distinct(ReferenceEqualityComparer.Instance).ToArray();
        Reserve(owners);
        IList[] lists = entries.Select(entry => entry.List)
            .Distinct<IList>(ReferenceEqualityComparer.Instance).ToArray();
        List<IList> reservedLists = [];
        bool committed = false;
        try
        {
            foreach (IList list in lists)
            {
                ReserveList(list);
                reservedLists.Add(list);
            }
            Preflight(entries, toApplied);
            ValidateFrozenSources(entries, toApplied);
            WritePrepared(entries, toApplied);
            committed = true;
        }
        finally
        {
            ReleaseLists(reservedLists);
            Release(owners);
            if (!committed)
                MarkOwners(entries);
        }
    }

    private static void Preflight(EditorListMutationEntry[] entries, bool toApplied)
    {
        foreach (EditorListMutationEntry entry in entries)
        {
            object?[] expected = toApplied ? entry.Before : entry.After;
            object token = toApplied ? entry.BeforeState : entry.AfterState;
            if (!entry.IsOwnerCurrent() || !Matches(entry.List, expected) ||
                !ReferenceEquals(States.GetValue(entry.List, _ => new()).Token, token))
                throw new InvalidOperationException("Undo was blocked because a list changed outside this edit.");
            if (toApplied && entry.Operation == EEditorListMutation.Insert ||
                !toApplied && entry.Operation == EEditorListMutation.Remove)
                ReserveCapacity(entry.List, entry.List.Count + 1);
        }
    }

    private static void WritePrepared(EditorListMutationEntry[] entries, bool toApplied)
    {
        ValidateFrozenSources(entries, toApplied);
        int completed = 0;
        try
        {
            for (; completed < entries.Length; completed++)
            {
                Mutate(entries[completed], toApplied);
                States.GetValue(entries[completed].List, _ => new()).Token = toApplied
                    ? entries[completed].AfterState : entries[completed].BeforeState;
            }
        }
        catch
        {
            for (int index = completed - 1; index >= 0; index--)
            {
                EditorListMutationEntry entry = entries[index];
                if (!Matches(entry.List, toApplied ? entry.After : entry.Before))
                    continue;
                try
                {
                    Mutate(entry, !toApplied);
                    States.GetValue(entry.List, _ => new()).Token = toApplied
                        ? entry.BeforeState : entry.AfterState;
                }
                catch { /* Keep the observed partial edit for manual recovery. */ }
            }
            throw;
        }
    }

    private static void ValidateFrozenSources(EditorListMutationEntry[] entries, bool toApplied)
    {
        foreach (EditorListMutationEntry entry in entries)
        {
            object?[] expected = toApplied ? entry.Before : entry.After;
            object token = toApplied ? entry.BeforeState : entry.AfterState;
            if (!entry.IsOwnerLocationCurrent() || !Matches(entry.List, expected) ||
                !ReferenceEquals(GetStateToken(entry.List), token))
                throw new InvalidOperationException("The list source changed before the edit could be written.");
        }
    }

    private static void ValidateViewSources(EditorListMutationContext? context, ulong revision,
        IReadOnlyList<IEditorListElementBinding>? bindings,
        IReadOnlyList<EditorListSourceLease>? sources)
    {
        if (context is not null && (!context.IsActive || context.Revision != revision))
            throw new InvalidOperationException("The list detail view changed before the edit.");
        if (bindings is not null)
            foreach (IEditorListElementBinding binding in bindings)
                if (!binding.IsLeased || binding.ContextRevision != revision ||
                    binding.Index < 0 || binding.Index >= binding.Context.Rows.Count ||
                    binding.Context.Rows.GetItemId(binding.Index) != binding.RowId ||
                    binding.Index >= binding.Collection.Count ||
                    !ReferenceEquals(GetStateToken(binding.Collection), binding.CollectionState) ||
                    !SameValue(binding.Collection[binding.Index], binding.DisplayedValue))
                    throw new InvalidOperationException("The list row changed before the edit.");
        if (sources is not null)
            foreach (EditorListSourceLease source in sources)
                if (source.Count != source.List.Count ||
                    !ReferenceEquals(GetStateToken(source.List), source.State))
                    throw new InvalidOperationException("The list command source changed before the edit.");
    }

    private static void Mutate(EditorListMutationEntry entry, bool toApplied)
        => EditorListSequenceKernel.Mutate(entry.List, entry.Operation, entry.Index,
            entry.Before, entry.After, toApplied);

    internal static bool Supports(IList list, EEditorListMutation operation)
    {
        Type type = list.GetType();
        if (type == typeof(ArrayList) || type.IsGenericType &&
            type.GetGenericTypeDefinition() == typeof(List<>))
            return IsComparableElementType(ElementType(list));
        return operation == EEditorListMutation.Set && type.IsArray &&
            type.GetArrayRank() == 1 && ((Array)list).GetLowerBound(0) == 0 &&
            IsComparableElementType(ElementType(list));
    }

    internal static bool SupportsSource(PropertyInfo property)
        => EditorListSourceAccessor.For(property) is not null;

    internal static bool IsComparableElementType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return !type.IsValueType || type.IsPrimitive || type.IsEnum ||
            type == typeof(decimal) || type == typeof(DateTime) || type == typeof(Guid) ||
            type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) ||
            type == typeof(Quaternion) || type == typeof(ColorF3) || type == typeof(ColorF4) ||
            type == typeof(LayerMask) || type == typeof(System.Drawing.Color);
    }

    private static Type ElementType(IList list)
        => list.GetType().IsArray ? list.GetType().GetElementType()! :
            list.GetType() == typeof(ArrayList) ? typeof(object) :
            list.GetType().GetGenericArguments()[0];

    private static object? ConvertElement(object? value, Type type)
    {
        if (value is null)
        {
            if (type.IsValueType && Nullable.GetUnderlyingType(type) is null)
                throw new InvalidOperationException("A value list cannot contain null.");
            return null;
        }
        if (!type.IsInstanceOfType(value))
            throw new InvalidOperationException("The value does not match the list element type.");
        return value;
    }

    internal static object? CreateDefaultElement(Type type)
        => type.IsValueType ? Activator.CreateInstance(type) :
            type == typeof(string) ? string.Empty : null;

    internal static bool SameValue(object? actual, object? expected)
        => EditorListSequenceKernel.SameValue(actual, expected);

    internal static object GetStateToken(IList list)
        => States.GetValue(list, _ => new()).Token;

    private static void ReserveCapacity(IList list, int count)
    {
        if (list is ArrayList arrayList && arrayList.Capacity < count)
            arrayList.Capacity = count;
        else if (list.GetType().IsGenericType && list.GetType().GetGenericTypeDefinition() == typeof(List<>))
        {
            PropertyInfo capacity = list.GetType().GetProperty("Capacity")!;
            if ((int)capacity.GetValue(list)! < count)
                capacity.SetValue(list, count);
        }
    }

    internal static void Reserve(object[] owners)
    {
        lock (BusyLock)
        {
            if (owners.Any(owner => BusyOwners.Contains(owner)))
                throw new InvalidOperationException("A list edit is already active for this owner.");
            foreach (object owner in owners) BusyOwners.Add(owner);
        }
    }

    internal static void Release(object[] owners)
    {
        lock (BusyLock)
            foreach (object owner in owners) BusyOwners.Remove(owner);
    }

    private static void ReserveList(IList list)
    {
        lock (BusyLock)
            if (!BusyLists.Add(list))
                throw new InvalidOperationException("A list edit is already active for this collection.");
    }

    /// <summary>Reserves nested source lists in the same owner/list domains as ordinary list edits.</summary>
    internal static IDisposable ReserveNestedMutation(object[] owners, IList[] lists)
    {
        Reserve(owners);
        List<IList> reserved = [];
        try
        {
            foreach (IList list in lists.Distinct<IList>(ReferenceEqualityComparer.Instance))
            {
                ReserveList(list);
                reserved.Add(list);
            }
            return new NestedReservation(owners, reserved);
        }
        catch
        {
            ReleaseLists(reserved);
            Release(owners);
            throw;
        }
    }

    /// <summary>Advances the shared list token only when the expected source generation still owns it.</summary>
    internal static object AdvanceNestedState(IList list, object expected, object next)
    {
        EditorListMutationState state = States.GetValue(list, _ => new());
        if (!ReferenceEquals(state.Token, expected))
            throw new InvalidOperationException("The nested list source changed during the edit.");
        state.Token = next;
        return next;
    }

    internal static void RestoreNestedState(IList list, object expected, object restore)
    {
        EditorListMutationState state = States.GetValue(list, _ => new());
        if (!ReferenceEquals(state.Token, expected))
            throw new InvalidOperationException("The nested list generation changed during compensation.");
        state.Token = restore;
    }

    private sealed class NestedReservation(object[] owners, List<IList> lists) : IDisposable
    {
        private object[]? _owners = owners;
        private List<IList>? _lists = lists;
        public void Dispose()
        {
            List<IList>? heldLists = Interlocked.Exchange(ref _lists, null);
            object[]? heldOwners = Interlocked.Exchange(ref _owners, null);
            if (heldLists is not null) ReleaseLists(heldLists);
            if (heldOwners is not null) Release(heldOwners);
        }
    }

    private static void ReleaseLists(List<IList> lists)
    {
        lock (BusyLock)
            foreach (IList list in lists) BusyLists.Remove(list);
    }

    internal static object? WorldOf(object owner)
        => owner is global::XREngine.XRWorldObjectBase worldOwner ? worldOwner.World : null;

    internal static object? DocumentOf(object owner)
        => EditorMutationDirtyState.ResolveDocument(owner);

    internal static XREngine.Scene.SceneNode? NodeOf(object owner)
        => owner is XREngine.Components.XRComponent component ? component.SceneNode : null;

    private static EditorListMutationEntry CopyWithOwners(EditorListMutationEntry entry,
        object[] owners, object?[] worlds, object?[] documents,
        XREngine.Scene.SceneNode?[] nodes) => new()
    {
        List = entry.List, Property = entry.Property, Owners = owners, OwnerWorlds = worlds,
        SourceAccessor = entry.SourceAccessor,
        OwnerDocuments = documents, OwnerNodes = nodes,
        Before = entry.Before, After = entry.After, BeforeState = entry.BeforeState,
        AfterState = entry.AfterState, Operation = entry.Operation, Index = entry.Index
    };

    internal static void MarkOwners(EditorListMutationEntry[] entries)
    {
        foreach (XREngine.Core.Files.XRAsset document in entries
            .SelectMany(entry => entry.OwnerDocuments).OfType<XREngine.Core.Files.XRAsset>()
            .Distinct<XREngine.Core.Files.XRAsset>(ReferenceEqualityComparer.Instance))
            try { document.MarkDirty(); }
            catch (Exception error) { Debug.LogException(error, "Could not mark an edited list owner dirty."); }
    }
}
