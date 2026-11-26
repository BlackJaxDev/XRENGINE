using System.Reflection;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Extensions;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>
/// Lists the objects that an inspector object field can assign: "None" for a nullable reference, then the
/// matching components, scene nodes, or transforms in the edited objects' world, or the matching asset files
/// in the game and engine asset folders. The list is built when the picker first asks for its count after
/// <see cref="Reset"/>, so a bind does not read the disk or walk the scene.
/// </summary>
internal sealed class InspectorObjectCandidateSource : IUIComboOptionSource
{
    /// <summary>The option id of the "None" entry, which clears the reference.</summary>
    public const ulong NoneId = 1;

    /// <summary>The most candidates that one picker lists.</summary>
    private const int MaxCandidates = 512;

    /// <summary>One entry of the picker: a live object, or an asset file that loads when it is picked.</summary>
    public readonly record struct Candidate(string Label, object? Value, string? AssetPath);

    private readonly List<Candidate> _candidates = [];
    private Type? _type;
    private object?[]? _targets;
    private bool _nullable;
    private bool _stale;
    private ulong _revision;

    public ulong Revision => _revision;

    public int Count
    {
        get
        {
            if (_stale)
                Refresh();
            return _candidates.Count;
        }
    }

    /// <summary>Sets the reference type and edited objects. The list is rebuilt the next time it is read.</summary>
    public void Reset(Type type, object?[]? targets, bool nullable)
    {
        _type = type;
        _targets = targets;
        _nullable = nullable;
        _stale = true;
    }

    /// <summary>Marks the list out of date, for example after a pick changed the scene.</summary>
    public void Invalidate() => _stale = true;

    public UIComboOption GetOption(int index)
        => new((ulong)index + NoneId, _candidates[index].Label);

    public int IndexOfId(ulong id)
    {
        long index = (long)id - (long)NoneId;
        return index >= 0 && index < _candidates.Count ? (int)index : -1;
    }

    /// <summary>Gets the candidate that a picker option id names.</summary>
    public bool TryGetCandidate(ulong id, out Candidate candidate)
    {
        int index = IndexOfId(id);
        candidate = index >= 0 ? _candidates[index] : default;
        return index >= 0;
    }

    private void Refresh()
    {
        _stale = false;
        _revision++;
        _candidates.Clear();
        if (_type is not { } type)
            return;
        if (_nullable)
            _candidates.Add(new("None", null, null));
        if (typeof(XRAsset).IsAssignableFrom(type))
        {
            foreach ((string path, string name) in ImGuiAssetUtilities.FindAssetFiles(type, MaxCandidates))
                _candidates.Add(new(name, null, path));
            return;
        }
        if (FindWorld(_targets) is not { } world)
            return;
        foreach (XRScene scene in world.TargetWorld?.Scenes ?? [])
        {
            foreach (SceneNode root in scene.SceneRoots)
            {
                root.IterateHierarchy(node =>
                {
                    if (_candidates.Count >= MaxCandidates)
                        return;
                    if (type.IsInstanceOfType(node))
                        _candidates.Add(new(node.Name ?? "Scene Node", node, null));
                    else if (node.Transform is { } transform && type.IsInstanceOfType(transform))
                        _candidates.Add(new(node.Name ?? "Scene Node", transform, null));
                    if (!typeof(XRComponent).IsAssignableFrom(type))
                        return;
                    foreach (XRComponent component in node.Components)
                        if (component is not null && type.IsInstanceOfType(component) && _candidates.Count < MaxCandidates)
                            _candidates.Add(new($"{node.Name} · {ComponentLabel(component)}", component, null));
                });
            }
        }
    }

    private static string ComponentLabel(XRComponent component)
        => string.IsNullOrWhiteSpace(component.Name) || component.Name == component.GetType().Name
            ? component.GetType().Name.SplitCamelCase()
            : component.Name;

    /// <summary>Finds the world of the first edited object that is in a scene.</summary>
    private static RuntimeWorld? FindWorld(object?[]? targets)
    {
        if (targets is null)
            return null;
        foreach (object? target in targets)
        {
            SceneNode? node = target switch
            {
                SceneNode sceneNode => sceneNode,
                XRComponent component => component.SceneNode,
                TransformBase transform => transform.SceneNode,
                _ => null,
            };
            if (node?.World is RuntimeWorld world && !world.IsDisposing)
                return world;
        }
        return null;
    }

    /// <summary>
    /// Returns true when the inspector edits a property of <paramref name="type"/> with an object field: a
    /// component, a scene node, a transform, or an asset.
    /// </summary>
    public static bool IsObjectReferenceType(Type type)
        => !type.IsValueType && (typeof(XRComponent).IsAssignableFrom(type) || typeof(SceneNode).IsAssignableFrom(type) ||
            typeof(TransformBase).IsAssignableFrom(type) || typeof(XRAsset).IsAssignableFrom(type));
}
