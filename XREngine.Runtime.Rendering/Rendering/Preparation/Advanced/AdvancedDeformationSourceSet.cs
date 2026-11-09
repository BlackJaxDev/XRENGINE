using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>Tracks the exact unique mesh inputs of one scene without per-frame allocations.</summary>
internal sealed class AdvancedDeformationSourceSet(int maximumMeshes)
{
    private readonly Dictionary<XRMesh, (AdvancedDeformationSourceWitness Witness, ulong Visit)> _sources =
        new(checked(2 * maximumMeshes), ReferenceEqualityComparer.Instance);
    private readonly XRMesh?[] _removed = new XRMesh?[maximumMeshes];
    private ulong _visit;
    private EAdvancedDeformationSourceChange _changes;
    private string? _changedMeshName;
    private bool _scanOpen;

    public ulong Generation { get; private set; }
    public int Count => _sources.Count;
    public EAdvancedDeformationSourceChange LastChanges { get; private set; }
    public string? LastChangedMeshName { get; private set; }

    public void Begin()
    {
        // A failed scan must not leave partial edits under an accepted generation.
        _changes = (_scanOpen ? EAdvancedDeformationSourceChange.InterruptedScan : 0) |
            (Generation == 0 ? EAdvancedDeformationSourceChange.Initial : 0);
        _changedMeshName = null;
        if (_scanOpen)
            _sources.Clear();
        _scanOpen = true;
        if (++_visit == 0)
        {
            _sources.Clear();
            _visit = 1;
            _changes |= EAdvancedDeformationSourceChange.Initial;
        }
    }

    public void Visit(XRMesh mesh, in AdvancedDeformationSourceWitness witness)
    {
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_sources, mesh, out bool exists);
        if (exists && entry.Visit == _visit)
        {
            if (!entry.Witness.Matches(in witness))
                throw new InvalidOperationException("One deformation mesh has conflicting captured source inputs.");
            return;
        }
        EAdvancedDeformationSourceChange changes = exists
            ? entry.Witness.GetChanges(in witness)
            : EAdvancedDeformationSourceChange.AddedMesh;
        _changes |= changes;
        if (changes != EAdvancedDeformationSourceChange.None)
            _changedMeshName ??= mesh.Name;
        entry = (witness, _visit);
    }

    public ulong Complete()
    {
        int removedCount = 0;
        foreach (var pair in _sources)
            if (pair.Value.Visit != _visit)
                _removed[removedCount++] = pair.Key;
        for (int index = 0; index < removedCount; index++)
        {
            _sources.Remove(_removed[index]!);
            _removed[index] = null;
        }
        if (removedCount != 0)
            _changes |= EAdvancedDeformationSourceChange.RemovedMesh;
        if (_changes != EAdvancedDeformationSourceChange.None)
        {
            LastChanges = _changes;
            LastChangedMeshName = _changedMeshName;
            if (++Generation == 0)
                Generation = 1;
        }
        _scanOpen = false;
        return Generation;
    }
}
