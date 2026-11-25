using System.Runtime.CompilerServices;
using XREngine.Core.Files;

namespace XREngine.Editor.UI;

/// <summary>Applies grouped edits to exact dictionaries with known key comparers.</summary>
internal static class EditorDictionaryMutation
{
    private static readonly ConditionalWeakTable<object, EditorDictionaryMutationState> States = new();
    private static readonly object BusyLock = new();
    private static readonly HashSet<object> BusyDictionaries = new(ReferenceEqualityComparer.Instance);

    internal static object StateOf(object dictionary) => States.GetValue(dictionary, _ => new()).Token;

    internal static EditorDictionarySourceLease Capture(object owner,
        System.Reflection.PropertyInfo property, Func<bool> isViewCurrent)
    {
        if (owner is IEditorListElementBinding or IEditorLinkedElementBinding or IEditorDictionaryElementBinding)
            throw new InvalidOperationException("Nested dictionary edits need a real source owner.");
        EditorListSourceAccessor accessor = EditorListSourceAccessor.For(property) ??
            throw new InvalidOperationException("Dictionary editing needs a nonvirtual auto-property source.");
        object dictionary = property.GetValue(owner) ??
            throw new InvalidOperationException("Dictionary editing needs a non-null source.");
        EditorDictionaryAdapter adapter = EditorDictionaryAdapter.For(dictionary) ??
            throw new InvalidOperationException("Only exact int or ordinal string dictionaries can be edited.");
        object? world = EditorListMutation.WorldOf(owner);
        object? document = EditorListMutation.DocumentOf(owner);
        var sceneNode = EditorListMutation.NodeOf(owner);
        if (owner is global::XREngine.XRWorldObjectBase && document is null)
            throw new InvalidOperationException("The dictionary owner has no unique source document.");
        EditorDictionarySourceLease lease = new()
        {
            Owner = owner, Property = property, Dictionary = dictionary,
            Adapter = adapter, Accessor = accessor, Displayed = adapter.Snapshot(dictionary),
            State = StateOf(dictionary), World = world, Document = document,
            SceneNode = sceneNode, IsViewCurrent = isViewCurrent
        };
        if (!lease.IsCurrent())
            throw new InvalidOperationException("The dictionary source changed during inspection.");
        return lease;
    }

    internal static void Set(IReadOnlyList<IEditorDictionaryElementBinding> bindings,
        Func<object?, object?> valueFactory, Action? refresh)
    {
        if (bindings.Count == 0) return;
        EditorDictionaryAdapter adapter = bindings[0].Source.Adapter;
        if (bindings.Any(binding => binding.Source.Adapter.KeyType != adapter.KeyType ||
            !adapter.KeyEquals(bindings[0].Key, binding.Key)))
            throw new InvalidOperationException("The dictionary rows do not share one key.");
        Apply(bindings.Select(binding => binding.Source).ToArray(),
            EEditorDictionaryMutation.Set, bindings[0].Key, valueFactory, refresh, bindings);
    }

    internal static void Add(EditorDictionarySourceLease[] sources, Action? refresh)
        => Apply(sources, EEditorDictionaryMutation.Add, null, null, refresh, null);

    internal static void Remove(EditorDictionarySourceLease[] sources, object key, Action? refresh)
        => Apply(sources, EEditorDictionaryMutation.Remove, key, null, refresh, null);

    private static void Apply(EditorDictionarySourceLease[] sources,
        EEditorDictionaryMutation operation, object? requestedKey,
        Func<object?, object?>? valueFactory, Action? refresh,
        IReadOnlyList<IEditorDictionaryElementBinding>? bindings)
    {
        if (sources.Length == 0) return;
        if (sources.Any(source => source.Adapter.KeyType != sources[0].Adapter.KeyType ||
            source.Adapter.ValueType != sources[0].Adapter.ValueType))
            throw new InvalidOperationException("The dictionary targets need matching key and value types.");
        object[] owners = sources.Select(source => source.Owner)
            .Distinct<object>(ReferenceEqualityComparer.Instance).ToArray();
        EditorListMutation.Reserve(owners);
        List<object> reserved = [];
        EditorDictionaryMutationEntry[] entries = [];
        IDisposable? interaction = null;
        Undo.ChangeScope? change = null;
        StructuralChangeHandle? hold = null;
        bool writesStarted = false;
        bool committed = false;
        try
        {
            foreach (object dictionary in sources.Select(source => source.Dictionary)
                .Distinct<object>(ReferenceEqualityComparer.Instance))
            {
                Reserve(dictionary);
                reserved.Add(dictionary);
            }
            foreach (EditorDictionarySourceLease source in sources)
                if (!source.IsCurrent())
                    throw new InvalidOperationException("The dictionary command source changed.");
            ValidateBindings(bindings);
            object key = operation == EEditorDictionaryMutation.Add
                ? FindNewKey(sources) : requestedKey ??
                    throw new InvalidOperationException("The dictionary row key is missing.");
            List<EditorDictionaryMutationEntry> prepared = [];
            foreach (IGrouping<object, EditorDictionarySourceLease> group in sources.GroupBy(
                source => source.Dictionary, ReferenceEqualityComparer.Instance))
            {
                EditorDictionarySourceLease first = group.First();
                EditorDictionarySnapshot before = first.Displayed;
                int position = first.Adapter.IndexOf(before, key);
                if (operation == EEditorDictionaryMutation.Add ? position >= 0 : position < 0)
                    throw new InvalidOperationException("The dictionary key changed while it was visible.");
                object actualKey = operation == EEditorDictionaryMutation.Add ? key : before.Keys[position];
                object? oldValue = position < 0 ? null : before.Values[position];
                prepared.Add(new EditorDictionaryMutationEntry
                {
                    Dictionary = first.Dictionary, Adapter = first.Adapter,
                    Owners = group.Select(source => new EditorDictionaryOwnerProof(source)).ToArray(),
                    Before = before, After = before, BeforeState = first.State,
                    AfterState = new object(), Operation = operation, Key = actualKey,
                    BeforeValue = oldValue, AfterValue = oldValue
                });
            }
            for (int slot = 0; slot < prepared.Count; slot++)
            {
                EditorDictionaryMutationEntry entry = prepared[slot];
                object? next = operation == EEditorDictionaryMutation.Set
                    ? valueFactory?.Invoke(entry.BeforeValue) :
                    operation == EEditorDictionaryMutation.Add
                        ? EditorListMutation.CreateDefaultElement(entry.Adapter.ValueType) : null;
                if (operation != EEditorDictionaryMutation.Remove && next is not null &&
                    !entry.Adapter.ValueType.IsInstanceOfType(next) ||
                    operation != EEditorDictionaryMutation.Remove && next is null &&
                    entry.Adapter.ValueType.IsValueType &&
                    Nullable.GetUnderlyingType(entry.Adapter.ValueType) is null)
                    throw new InvalidOperationException("The value does not match the dictionary value type.");
                object[] keys = operation == EEditorDictionaryMutation.Add
                    ? [.. entry.Before.Keys, entry.Key] :
                    operation == EEditorDictionaryMutation.Remove
                        ? entry.Before.Keys.Where(existing => !entry.Adapter.KeyEquals(existing, entry.Key)).ToArray()
                        : [.. entry.Before.Keys];
                object?[] values = operation == EEditorDictionaryMutation.Add
                    ? [.. entry.Before.Values, next] :
                    operation == EEditorDictionaryMutation.Remove
                        ? entry.Before.Values.Where((_, i) =>
                            !entry.Adapter.KeyEquals(entry.Before.Keys[i], entry.Key)).ToArray()
                        : [.. entry.Before.Values];
                if (operation == EEditorDictionaryMutation.Set)
                    values[entry.Adapter.IndexOf(entry.Before, entry.Key)] = next;
                prepared[slot] = new EditorDictionaryMutationEntry
                {
                    Dictionary = entry.Dictionary, Adapter = entry.Adapter, Owners = entry.Owners,
                    Before = entry.Before, After = new(keys, values, entry.Adapter.KeyType),
                    BeforeState = entry.BeforeState, AfterState = entry.AfterState,
                    Operation = entry.Operation, Key = entry.Key,
                    BeforeValue = entry.BeforeValue, AfterValue = next
                };
            }
            entries = [.. prepared];
            if (operation == EEditorDictionaryMutation.Set && entries.All(entry =>
                EditorListMutation.SameValue(entry.BeforeValue, entry.AfterValue)))
                return;
            Preflight(entries, true);
            ValidateBindings(bindings);
            ValidateFrozen(entries, true);
            interaction = Undo.BeginUserInteraction();
            change = Undo.BeginChange("Edit dictionary");
            EditorDictionaryMutationReceipt receipt = new(entries, refresh);
            hold = Undo.BeginStructuralMutation("Edit dictionary", receipt.Undo, receipt.Redo);
            try
            {
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
                    foreach (IEditorDictionaryElementBinding binding in bindings)
                        try
                        {
                            EditorDictionaryMutationEntry? entry = entries.FirstOrDefault(candidate =>
                                ReferenceEquals(candidate.Dictionary, binding.Source.Dictionary));
                            if (entry is not null)
                            {
                                binding.Source.AcceptCommitted(entry.After, entry.AfterState);
                                binding.AcceptCommitted(entry.AfterValue, entry.AfterState);
                            }
                        }
                        catch (Exception error)
                        { Debug.LogException(error, "Could not refresh a committed dictionary binding."); }
                try
                {
                    try
                    {
                        try { change?.Dispose(); }
                        catch (Exception error) when (committed)
                        { Debug.LogException(error, "Undo notification failed after the dictionary edit committed."); }
                    }
                    finally
                    {
                        try { interaction?.Dispose(); }
                        catch (Exception error) when (committed)
                        { Debug.LogException(error, "Undo interaction notification failed after the dictionary edit committed."); }
                    }
                }
                finally
                {
                    try
                    {
                        if (committed && operation != EEditorDictionaryMutation.Set)
                            refresh?.Invoke();
                    }
                    catch (Exception error) { Debug.LogException(error, "Could not refresh dictionary rows."); }
                    finally
                    {
                        if (committed || writesStarted) MarkOwners(entries);
                    }
                }
            }
            finally
            {
                Release(reserved);
                EditorListMutation.Release(owners);
                hold?.Dispose();
            }
        }
    }

    internal static void Replay(EditorDictionaryMutationEntry[] entries, bool toApplied)
    {
        object[] owners = entries.SelectMany(entry => entry.Owners).Select(proof => proof.Owner)
            .Distinct<object>(ReferenceEqualityComparer.Instance).ToArray();
        EditorListMutation.Reserve(owners);
        List<object> reserved = [];
        bool writesStarted = false;
        bool committed = false;
        try
        {
            foreach (object dictionary in entries.Select(entry => entry.Dictionary)
                .Distinct<object>(ReferenceEqualityComparer.Instance))
            {
                Reserve(dictionary);
                reserved.Add(dictionary);
            }
            Preflight(entries, toApplied);
            ValidateFrozen(entries, toApplied);
            writesStarted = true;
            Write(entries, toApplied);
            committed = true;
        }
        finally
        {
            Release(reserved);
            EditorListMutation.Release(owners);
            if (!committed && writesStarted) MarkOwners(entries);
        }
    }

    private static object FindNewKey(EditorDictionarySourceLease[] sources)
    {
        if (sources[0].Adapter.KeyType == typeof(string))
        {
            for (int number = 0; number < 100000; number++)
            {
                string key = number == 0 ? "Key" : $"Key {number}";
                if (sources.All(source => source.Adapter.IndexOf(source.Displayed, key) < 0))
                    return key;
            }
        }
        else
        {
            for (int key = 0; key < 100000; key++)
                if (sources.All(source => source.Adapter.IndexOf(source.Displayed, key) < 0))
                    return key;
        }
        throw new InvalidOperationException("No common dictionary key is available.");
    }

    private static void ValidateBindings(IReadOnlyList<IEditorDictionaryElementBinding>? bindings)
    {
        if (bindings is null) return;
        foreach (IEditorDictionaryElementBinding binding in bindings)
        {
            EditorDictionarySourceLease source = binding.Source;
            int index = source.Adapter.IndexOf(source.Displayed, binding.Key);
            if (!binding.IsCurrent || index < 0 ||
                !source.Adapter.SameKey(binding.Key, source.Displayed.Keys[index]) ||
                !EditorListMutation.SameValue(binding.DisplayedValue,
                    source.Displayed.Values[index]))
                throw new InvalidOperationException("The dictionary row changed while it was visible.");
        }
    }

    private static void Preflight(EditorDictionaryMutationEntry[] entries, bool toApplied)
    {
        foreach (EditorDictionaryMutationEntry entry in entries)
            if (!entry.IsOwnerCurrent())
                throw new InvalidOperationException("Undo was blocked because a dictionary owner changed.");
        ValidateFrozen(entries, toApplied);
        foreach (EditorDictionaryMutationEntry entry in entries)
        {
            bool needsAdd = entry.Operation == EEditorDictionaryMutation.Add && toApplied ||
                entry.Operation == EEditorDictionaryMutation.Remove && !toApplied;
            if (needsAdd)
                entry.Adapter.ReserveCapacity(entry.Dictionary, entry.Before.Keys.Length + 1);
        }
        ValidateFrozen(entries, toApplied);
    }

    private static void ValidateFrozen(EditorDictionaryMutationEntry[] entries, bool toApplied)
    {
        foreach (EditorDictionaryMutationEntry entry in entries)
            if (!entry.IsOwnerLocationCurrent() || !Matches(entry, toApplied))
                throw new InvalidOperationException("The dictionary changed before the edit could be written.");
    }

    private static bool Matches(EditorDictionaryMutationEntry entry, bool before)
        => ReferenceEquals(StateOf(entry.Dictionary),
               before ? entry.BeforeState : entry.AfterState) &&
           entry.Adapter.Matches(entry.Dictionary, before ? entry.Before : entry.After);

    private static void Write(EditorDictionaryMutationEntry[] entries, bool toApplied)
    {
        ValidateFrozen(entries, toApplied);
        int completed = 0;
        try
        {
            for (; completed < entries.Length; completed++)
            {
                Mutate(entries[completed], toApplied);
                States.GetValue(entries[completed].Dictionary, _ => new()).Token = toApplied
                    ? entries[completed].AfterState : entries[completed].BeforeState;
                if (!entries[completed].Adapter.Matches(entries[completed].Dictionary,
                    toApplied ? entries[completed].After : entries[completed].Before))
                    throw new InvalidOperationException("The dictionary changed during the edit.");
            }
        }
        catch (Exception error)
        {
            for (int index = completed; index >= 0; index--)
            {
                if (index >= entries.Length) continue;
                EditorDictionaryMutationEntry entry = entries[index];
                if (!Matches(entry, !toApplied)) continue;
                try
                {
                    Mutate(entry, !toApplied);
                    States.GetValue(entry.Dictionary, _ => new()).Token = toApplied
                        ? entry.BeforeState : entry.AfterState;
                }
                catch { /* Keep the observed conflict for manual recovery. */ }
            }
            if (!entries.All(entry => Matches(entry, toApplied)))
                throw new InvalidOperationException(
                    "The dictionary edit has a partial conflict and needs manual recovery.", error);
            throw;
        }
    }

    private static void Mutate(EditorDictionaryMutationEntry entry, bool toApplied)
    {
        switch (entry.Operation)
        {
            case EEditorDictionaryMutation.Set:
                entry.Adapter.Set(entry.Dictionary, entry.Key,
                    toApplied ? entry.AfterValue : entry.BeforeValue);
                break;
            case EEditorDictionaryMutation.Add:
                if (toApplied) entry.Adapter.Add(entry.Dictionary, entry.Key, entry.AfterValue);
                else entry.Adapter.Remove(entry.Dictionary, entry.Key);
                break;
            case EEditorDictionaryMutation.Remove:
                if (toApplied) entry.Adapter.Remove(entry.Dictionary, entry.Key);
                else entry.Adapter.Add(entry.Dictionary, entry.Key, entry.BeforeValue);
                break;
        }
    }

    private static void Reserve(object dictionary)
    {
        lock (BusyLock)
            if (!BusyDictionaries.Add(dictionary))
                throw new InvalidOperationException("A dictionary edit is already active for this collection.");
    }

    private static void Release(List<object> dictionaries)
    {
        lock (BusyLock)
            foreach (object dictionary in dictionaries) BusyDictionaries.Remove(dictionary);
    }

    internal static void MarkOwners(EditorDictionaryMutationEntry[] entries)
    {
        foreach (XRAsset document in entries.SelectMany(entry => entry.Owners)
            .Select(proof => proof.Document).OfType<XRAsset>()
            .Distinct<XRAsset>(ReferenceEqualityComparer.Instance))
            try { document.MarkDirty(); }
            catch (Exception error) { Debug.LogException(error, "Could not mark an edited dictionary owner dirty."); }
    }
}
