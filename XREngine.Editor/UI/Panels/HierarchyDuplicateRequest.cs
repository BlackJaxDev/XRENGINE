using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Retains the exact selected root and document through menu dismissal and confirmation.</summary>
internal sealed class HierarchyDuplicateRequest
{
    private readonly HierarchyCommandContext _context;
    private readonly HierarchyCloneDocument _document;
    private readonly HierarchyDuplicateSource? _source;
    private readonly HierarchyPlacement? _placement;
    private readonly SceneNode[] _targets;
    private readonly HierarchyDuplicateAssetPlan? _assets;
    private int _execution;

    internal HierarchyDuplicateRequest(SceneNode[] targets, RuntimeWorld world, Func<bool> ownerIsCurrent)
    {
        _targets = [.. targets];
        _context = new(world, _targets, ownerIsCurrent);
        _document = new(world);
        if (_targets.Length == 1)
        {
            _source = new(_targets[0]);
            _placement = HierarchyPlacement.Capture(_targets[0], world);
            RequireCurrent();
            try { _assets = new(_source); }
            catch (InvalidOperationException failure) { OrdinaryUnavailable = failure.Message; }
        }
    }

    internal string? UnsupportedReason => _targets.Length == 1 ? null : "Duplicate one selected root at a time.";
    internal string? OrdinaryUnavailable { get; }
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
        HierarchyDuplicateAssetPlan? assets = preserveAssetReferences ? _assets! : null;
        string name = EditorImGuiUI.GetNativeHierarchyDuplicateName(_targets[0], _context.World);
        Action? requireCloneAssets = null;
        void RequireSource()
        {
            RequireCurrent();
            assets?.RequireCurrent();
            requireCloneAssets?.Invoke();
        }
        return HierarchyCloneMutation.Execute(preserveAssetReferences ? "Duplicate Node" : "Deep Duplicate Node",
            _targets[0], _context.World, clone => _placement!.Parent is { } parent
                ? HierarchyPlacement.Attached(clone, _context.World, parent)
                : HierarchyPlacement.RootLike(clone, _placement!), onApplied,
            () => { RequireSource(); return true; }, store,
            configure: (clone, construction, validateClone) =>
            {
                void RequireBoundary() { RequireSource(); validateClone(); }
                RequireBoundary();
                XRWorldObjectBase[] cloneTargets = _source!.MatchClone(clone, construction);
                if (assets is not null)
                    requireCloneAssets = assets.Apply(cloneTargets, RequireBoundary);
                RequireBoundary();
                clone.Name = name;
                RequireBoundary();
            }, requireSource: RequireSource, selectClone: true);
    }

    private void RequireCurrent()
    {
        if (UnsupportedReason is { } reason)
            throw new InvalidOperationException(reason);
        if (!_context.CanStart())
            throw new InvalidOperationException("The duplicate source or hierarchy owner changed before execution.");
        _document.RequireOperationRoot(_targets[0]);
        _source!.RequireCurrent();
    }
}
