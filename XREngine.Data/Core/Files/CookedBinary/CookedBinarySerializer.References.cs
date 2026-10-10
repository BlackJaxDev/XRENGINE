using System.Diagnostics.CodeAnalysis;
using XREngine.Data;

namespace XREngine.Core.Files;

public sealed unsafe partial class CookedBinaryWriter
{
    private Dictionary<object, int> _referenceIds = new(ReferenceEqualityComparer.Instance);

    internal IDisposable EnterIndependentReferenceScope()
    {
        Dictionary<object, int> previous = _referenceIds;
        _referenceIds = new(ReferenceEqualityComparer.Instance);
        return new WriterReferenceScope(this, previous);
    }

    private sealed class WriterReferenceScope(CookedBinaryWriter writer, Dictionary<object, int> previous) : IDisposable
    {
        public void Dispose() => writer._referenceIds = previous;
    }

    internal bool WriteReferenceHeader(object value)
    {
        if (_referenceIds.TryGetValue(value, out int existingId))
        {
            Write((byte)CookedBinaryTypeMarker.Reference);
            Write(existingId);
            return true;
        }

        int id = _referenceIds.Count;
        _referenceIds.Add(value, id);
        Write((byte)CookedBinaryTypeMarker.ReferenceDefinition);
        Write(id);
        return false;
    }
}

public sealed unsafe partial class CookedBinaryReader
{
    private List<object?> _references = [];
    private List<bool> _completedReferences = [];
    private List<bool> _referencedBeforeCompletion = [];

    internal IDisposable EnterIndependentReferenceScope()
    {
        List<object?> previousReferences = _references;
        List<bool> previousCompleted = _completedReferences;
        List<bool> previousEarlyUse = _referencedBeforeCompletion;
        _references = [];
        _completedReferences = [];
        _referencedBeforeCompletion = [];
        return new ReaderReferenceScope(this, previousReferences, previousCompleted, previousEarlyUse);
    }

    private sealed class ReaderReferenceScope(CookedBinaryReader reader, List<object?> references,
        List<bool> completed, List<bool> earlyUse) : IDisposable
    {
        public void Dispose()
        {
            reader._references = references;
            reader._completedReferences = completed;
            reader._referencedBeforeCompletion = earlyUse;
        }
    }

    internal int BeginReferenceDefinition()
    {
        int id = ReadInt32();
        if (id != _references.Count)
            throw new InvalidDataException($"CookedBinary.InvalidReferenceDefinition: expected {_references.Count}, found {id}.");
        CookedBinaryReadBudget.ValidateCount(id);
        _references.Add(null);
        _completedReferences.Add(false);
        _referencedBeforeCompletion.Add(false);
        return id;
    }

    internal void RegisterReference(int id, object? value)
    {
        if (value is null || id < 0 || id >= _references.Count || _references[id] is not null)
            throw new InvalidDataException($"CookedBinary.InvalidReferenceDefinition: reference {id} cannot be registered.");
        _references[id] = value;
    }

    internal object ReadReference()
    {
        int id = ReadInt32();
        if (id < 0 || id >= _references.Count || _references[id] is not { } value)
            throw new InvalidDataException($"CookedBinary.UnresolvedReference: reference {id} is unavailable or its codec does not support cycles.");
        if (!_completedReferences[id])
            _referencedBeforeCompletion[id] = true;
        return value;
    }

    internal void CompleteReferenceDefinition(int id, object value)
    {
        if (id < 0 || id >= _references.Count || _references[id] is null || _completedReferences[id])
            throw new InvalidDataException($"CookedBinary.InvalidReferenceDefinition: reference {id} cannot be completed.");
        if (_referencedBeforeCompletion[id] && !ReferenceEquals(_references[id], value))
            throw new InvalidDataException($"CookedBinary.ReferenceReplacementCycleUnsupported: callback replaced reference {id} after a cycle used it.");
        _references[id] = value;
        _completedReferences[id] = true;
    }
}

public static partial class CookedBinarySerializer
{
    private static bool IsGraphReferenceCandidate(object value, Type runtimeType)
        => !runtimeType.IsValueType
            && value is not string and not byte[] and not DataSource and not Type
            && !PrimitiveWriters.ContainsKey(runtimeType);

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object ReadReferenceDefinition(CookedBinaryReader reader, CookedBinarySerializationCallbacks? callbacks, out int referenceId)
    {
        int id = reader.BeginReferenceDefinition();
        referenceId = id;
        CookedBinaryTypeMarker marker = (CookedBinaryTypeMarker)reader.ReadByte();
        return marker switch
        {
            CookedBinaryTypeMarker.Array => ReadArray(reader, callbacks, id),
            CookedBinaryTypeMarker.List => ReadList(reader, callbacks, id),
            CookedBinaryTypeMarker.Dictionary => ReadDictionary(reader, callbacks, id),
            CookedBinaryTypeMarker.HashSet => ReadHashSet(reader, callbacks, id),
            CookedBinaryTypeMarker.Object => ReadObject(reader, callbacks, id)
                ?? throw new InvalidDataException($"CookedBinary.ConstructionFailed: reference {id} could not be restored."),
            CookedBinaryTypeMarker.CustomObject or CookedBinaryTypeMarker.XREvent or CookedBinaryTypeMarker.XREventGeneric
                => ReadCompletedReferenceDefinition(reader, marker, callbacks, id),
            _ => throw new InvalidDataException($"CookedBinary.InvalidReferenceDefinition: marker '{marker}' cannot define a reference.")
        };
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object ReadCompletedReferenceDefinition(
        CookedBinaryReader reader, CookedBinaryTypeMarker marker,
        CookedBinarySerializationCallbacks? callbacks, int id)
    {
        foreach (CookedBinaryModule module in SerializationModules)
        {
            if (!module.TryRead(marker, reader, expectedType: null, callbacks, out object? value))
                continue;
            return RegisterCompletedReference(reader, id, value)
                ?? throw new InvalidDataException($"CookedBinary.ConstructionFailed: reference {id} could not be restored.");
        }

        throw new InvalidDataException($"CookedBinary.InvalidReferenceDefinition: marker '{marker}' has no decoder.");
    }

    private static object? RegisterCompletedReference(CookedBinaryReader reader, int id, object? value)
    {
        if (id >= 0)
            reader.RegisterReference(id, value);
        return value;
    }
}
