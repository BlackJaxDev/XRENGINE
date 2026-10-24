using System;
using System.Collections.Generic;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public partial class HierarchyPanel
{
    private readonly Dictionary<XRScene, XRPropertyChangedEventHandler> _sourceSceneListeners = [];

    private void SubscribeSourceNode(SceneNode node)
    {
        if (!CanUseHierarchy || _sourceListeners.ContainsKey(node))
            return;
        HierarchySourceBinding binding = new(node, node.Transform);
        binding.Added = child =>
        {
            if (IsCurrentSourceBinding(binding) && ReferenceEquals(node.Transform, binding.Transform))
                OnSourceChildAdded(node, child);
        };
        binding.Removed = child =>
        {
            if (IsCurrentSourceBinding(binding) && ReferenceEquals(node.Transform, binding.Transform))
                OnSourceChildRemoved(child);
        };
        binding.Changed = XRPropertyNotificationHandlers.FilterChanged((sender, args) =>
        {
            if (ReferenceEquals(sender, node) && IsCurrentSourceBinding(binding))
                OnSourceNodeChanged(node, args.PropertyName);
        }, nameof(SceneNode.Name), nameof(SceneNode.IsActiveSelf), nameof(SceneNode.CanDeactivate), nameof(SceneNode.Transform));
        _sourceListeners.Add(node, binding);
        binding.Children.PostAnythingAdded += binding.Added;
        binding.Children.PostAnythingRemoved += binding.Removed;
        node.PropertyChanged += binding.Changed;
    }

    private bool IsCurrentSourceBinding(HierarchySourceBinding binding)
        => CanUseHierarchy && !binding.Released &&
            _sourceListeners.TryGetValue(binding.Node, out HierarchySourceBinding? current) &&
            ReferenceEquals(current, binding);

    private static void ReleaseSourceBinding(HierarchySourceBinding binding)
    {
        binding.Released = true;
        binding.Children.PostAnythingAdded -= binding.Added;
        binding.Children.PostAnythingRemoved -= binding.Removed;
        binding.Node.PropertyChanged -= binding.Changed;
        binding.Added = null;
        binding.Removed = null;
        binding.Changed = null;
    }

    private void UnsubscribeSourceNode(SceneNode node)
    {
        if (!_sourceListeners.Remove(node, out HierarchySourceBinding? binding))
            return;
        ReleaseSourceBinding(binding);
        foreach (SceneNode child in GetSceneNodeChildren(node))
            UnsubscribeSourceNode(child);
    }

    private void UnsubscribeAllSourceNodes()
    {
        foreach (HierarchySourceBinding binding in _sourceListeners.Values)
            ReleaseSourceBinding(binding);
        _sourceListeners.Clear();
        foreach (var pair in _sourceSceneListeners)
            pair.Key.PropertyChanged -= pair.Value;
        _sourceSceneListeners.Clear();
    }

    private void SubscribeSourceScene(XRScene scene)
    {
        if (_sourceSceneListeners.ContainsKey(scene))
            return;
        XRPropertyChangedEventHandler? callback = null;
        callback = XRPropertyNotificationHandlers.FilterChanged((sender, args) =>
        {
            if (!CanUseHierarchy || !ReferenceEquals(sender, scene) ||
                !_sourceSceneListeners.TryGetValue(scene, out XRPropertyChangedEventHandler? current) ||
                !ReferenceEquals(current, callback))
                return;
            if (args.PropertyName == nameof(XRScene.IsEditorOnly))
                RebuildVirtualTree();
            else
                RefreshSourceScene(scene);
        }, nameof(XRScene.Name), nameof(XRScene.IsDirty), nameof(XRScene.IsVisible), nameof(XRScene.IsEditorOnly));
        _sourceSceneListeners.Add(scene, callback);
        scene.PropertyChanged += callback;
    }

    private void RefreshSourceScene(XRScene scene)
    {
        if (_virtualTree is null || !_sceneIds.TryGetValue(scene.ID, out ulong id) ||
            !_virtualTree.TryGetNode(id, out UIVirtualTreeNode? item) || item is null)
            return;
        _virtualTree.Rename(id, GetSceneRowName(scene));
        uint revision = _hierarchyRevision;
        foreach (HierarchyRow row in _hierarchyRows.Values)
        {
            if (ReferenceEquals(row.Scene, scene))
                BindHierarchyRow(row.Transform, item, 0, 0);
            if (!CanUseHierarchy || revision != _hierarchyRevision)
                return;
        }
    }

    private static string GetSceneRowName(XRScene scene)
    {
        string name = string.IsNullOrWhiteSpace(scene.Name) ? "Untitled Scene" : scene.Name!;
        return scene.IsDirty ? name + " *" : name;
    }

    private void OnSourceNodeChanged(SceneNode node, string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(SceneNode.Name):
                RenameVirtualNode(node, node.Name ?? SceneNode.DefaultName);
                break;
            case nameof(SceneNode.IsActiveSelf):
            case nameof(SceneNode.CanDeactivate):
                RefreshSourceNodeRows(node);
                break;
            case nameof(SceneNode.Transform):
                // The node can replace the whole child source. Retire every old exact
                // subscription before the new topology is read.
                RebuildVirtualTree();
                break;
        }
    }

    private void RefreshSourceNodeRows(SceneNode node)
    {
        if (!CanUseHierarchy || _virtualTree is null ||
            !_hierarchyIds.TryGetValue(node.ID, out ulong id) ||
            !_virtualTree.TryGetNode(id, out UIVirtualTreeNode? item) || item is null)
            return;
        uint revision = _hierarchyRevision;
        foreach (HierarchyRow row in _hierarchyRows.Values)
        {
            if (ReferenceEquals(row.Node, node))
                BindHierarchyRow(row.Transform, item, 0, 0);
            if (!CanUseHierarchy || revision != _hierarchyRevision)
                return;
        }
    }
}
