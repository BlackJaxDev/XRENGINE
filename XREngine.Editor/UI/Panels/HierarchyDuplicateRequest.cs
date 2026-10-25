using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Retains the exact selected root and document through menu dismissal and confirmation.</summary>
internal sealed class HierarchyDuplicateRequest
{
    private readonly HierarchyCommandContext _context;
    private readonly HierarchyCloneDocument _document;
    private readonly HierarchyDuplicateSource[] _sources;
    private readonly HierarchyPlacement[] _placements;
    private readonly SceneNode[] _targets;
    private readonly string?[] _sourceNames;
    private readonly HierarchyDuplicateAssetPlan?[] _assets;
    private int _execution;

    internal HierarchyDuplicateRequest(SceneNode[] targets, RuntimeWorld world, Func<bool> ownerIsCurrent)
    {
        _targets = [.. targets];
        _sourceNames = _targets.Select(target => target.Name).ToArray();
        _context = new(world, _targets, ownerIsCurrent);
        _document = new(world);
        HashSet<SceneNode> unique = new(ReferenceEqualityComparer.Instance);
        foreach (SceneNode target in _targets)
        {
            if (!unique.Add(target))
                throw new InvalidOperationException("A grouped duplicate repeats a source root.");
            for (SceneNode? ancestor = target.Parent; ancestor is not null; ancestor = ancestor.Parent)
                if (_targets.Any(candidate => ReferenceEquals(candidate, ancestor)))
                    throw new InvalidOperationException("A grouped duplicate contains an ancestor and its child.");
        }
        _sources = new HierarchyDuplicateSource[_targets.Length];
        _placements = new HierarchyPlacement[_targets.Length];
        _assets = new HierarchyDuplicateAssetPlan?[_targets.Length];
        if (_targets.Length > 0)
        {
            for (int i = 0; i < _targets.Length; i++)
            {
                _sources[i] = new(_targets[i]);
                _placements[i] = HierarchyPlacement.Capture(_targets[i], world);
            }
            RequireCurrent();
            for (int i = 0; i < _targets.Length; i++)
            {
                try { _assets[i] = new(_sources[i]); }
                catch (InvalidOperationException failure) { OrdinaryUnavailable ??= failure.Message; }
            }
        }
    }

    internal string? UnsupportedReason => _targets.Length > 0 ? null : "Select a hierarchy root to duplicate.";
    internal string? OrdinaryUnavailable { get; private set; }
    internal string Name => _targets.Length == 1 ? _targets[0].Name ?? SceneNode.DefaultName : "selected nodes";
    internal bool TryQueue() => Interlocked.CompareExchange(ref _execution, 1, 0) == 0;

    internal bool CanStart()
    {
        try { RequireCurrent(); return true; }
        catch (InvalidOperationException) { return false; }
    }

    internal HierarchyCloneCustody Execute(bool preserveAssetReferences, HierarchyCloneCustodyStore store,
        Action<SceneNode>? onApplied = null)
    {
        if (Interlocked.Exchange(ref _execution, 2) == 2)
            throw new InvalidOperationException("The duplicate request has already executed.");
        RequireCurrent();
        if (preserveAssetReferences && OrdinaryUnavailable is { } unsupported)
            throw new InvalidOperationException(unsupported);
        HierarchyDuplicateAssetPlan?[] assets = preserveAssetReferences ? _assets : new HierarchyDuplicateAssetPlan?[_targets.Length];
        string[] names = EditorImGuiUI.GetNativeHierarchyDuplicateNames(_targets, _context.World);
        RequireCurrent();
        SceneNode?[] cloneRoots = new SceneNode?[_targets.Length];
        bool[] named = new bool[_targets.Length];
        Action?[] requireCloneAssets = new Action?[_targets.Length];
        void RequireSource()
        {
            RequireCurrent();
            for (int i = 0; i < assets.Length; i++)
            {
                assets[i]?.RequireCurrent();
                requireCloneAssets[i]?.Invoke();
                if (!EditorImGuiUI.IsNativeHierarchyDuplicateNameAvailable(
                        _targets[i], _context.World, names[i], cloneRoots))
                    throw new InvalidOperationException("A planned duplicate name became occupied.");
                if (named[i] && cloneRoots[i]?.Name != names[i])
                    throw new InvalidOperationException("A clone's assigned duplicate name changed.");
            }
        }
        HierarchyPlacement Destination(int index, SceneNode clone)
            => _placements[index].Parent is { } parent
                ? HierarchyPlacement.Attached(clone, _context.World, parent)
                : HierarchyPlacement.RootLike(clone, _placements[index]);
        void Configure(int index, SceneNode clone, ObjectConstructionContext construction, Action validateClone)
        {
            cloneRoots[index] = clone;
            void RequireBoundary() { RequireSource(); validateClone(); }
            RequireBoundary();
            XRWorldObjectBase[] cloneTargets = _sources[index].MatchClone(clone, construction);
            if (assets[index] is { } plan)
                requireCloneAssets[index] = plan.Apply(cloneTargets, RequireBoundary);
            RequireBoundary();
            clone.Name = names[index];
            named[index] = true;
            RequireBoundary();
        }
        if (_targets.Length > 1)
            return HierarchyCloneMutation.ExecuteMany(
                preserveAssetReferences ? "Duplicate Nodes" : "Deep Duplicate Nodes",
                _targets, _context.World, Destination, onApplied,
                () => { RequireSource(); return true; }, store,
                Configure, RequireSource, selectClones: true);
        return HierarchyCloneMutation.Execute(preserveAssetReferences ? "Duplicate Node" : "Deep Duplicate Node",
            _targets[0], _context.World, clone => Destination(0, clone), onApplied,
            () => { RequireSource(); return true; }, store,
            configure: (clone, construction, validateClone) => Configure(0, clone, construction, validateClone),
            requireSource: RequireSource, selectClone: true);
    }

    private void RequireCurrent()
    {
        if (UnsupportedReason is { } reason)
            throw new InvalidOperationException(reason);
        if (!_context.CanStart())
            throw new InvalidOperationException("The duplicate source or hierarchy owner changed before execution.");
        for (int i = 0; i < _targets.Length; i++)
        {
            _document.RequireOperationRoot(_targets[i]);
            _sources[i].RequireCurrent();
            if (_targets[i].Name != _sourceNames[i])
                throw new InvalidOperationException("A duplicate source name changed before execution.");
            if (!_placements[i].Matches())
                throw new InvalidOperationException("A duplicate source placement changed.");
        }
    }
}
