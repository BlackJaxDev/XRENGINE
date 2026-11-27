using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Transforms;
using XREngine.Components.VR;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Hides the local VR avatar head in first-person views while mirrors and scene captures keep the
/// full avatar head. The component leaves the real avatar skeleton at full scale on
/// <see cref="DefaultLayers.LocalVrAvatarIndex"/> and creates first-person head-chopped proxies on
/// <see cref="DefaultLayers.LocalVrFirstPersonIndex"/>.
/// </summary>
public sealed class VrLocalAvatarVisibilityComponent : XRComponent
{
    private const float HiddenHeadScale = 0.0001f;

    private readonly List<VrHeadChopComponent> _headChopComponents = [];
    private readonly List<OverrideTarget> _overrideTargets = [];
    private readonly List<ModelComponent> _discoveredModels = [];
    private readonly List<SkinnedProxyEntry> _skinnedProxies = [];
    private readonly HashSet<SceneNode> _proxyNodeSet = new(ReferenceEqualityComparer.Instance);
    private readonly List<SavedNodeLayer> _savedNodeLayers = [];
    private readonly Dictionary<TransformBase, BoneChopRule> _boneChopRules = new(ReferenceEqualityComparer.Instance);
    private readonly Func<TransformBase, Matrix4x4, Matrix4x4> _boneMatrixModifier;
    private readonly Action<ModelComponent> _collectModelAction;
    private readonly Action<HumanoidComponent> _collectHumanoidAction;
    private readonly Action<VrHeadChopComponent> _collectHeadChopAction;
    private SceneNode? _avatarRoot;
    private SceneNode? _headNode;
    private HumanoidComponent? _discoveredHumanoid;
    private bool _headHidingEnabled = true;
    private bool _splitApplied;
    private bool _lastHeadChopVrState;
    private bool _boneRulesBuilt;

    private readonly record struct OverrideTarget(SceneNode Node, int DepthUnderHead, float ScaleFactor);
    private readonly record struct BoneChopRule(float ScaleFactor, bool KeepWorldOffset);
    private readonly record struct SavedNodeLayer(SceneNode Node, ModelComponent Model, int OriginalLayer);

    private sealed class SkinnedProxyEntry
    {
        public required ModelComponent SourceModel;
        public required SceneNode ProxyNode;
        public required ModelComponent ProxyModel;
        public Rendering.Models.Model? BoundModelAsset;
    }

    public VrLocalAvatarVisibilityComponent()
    {
        _boneMatrixModifier = TransformFirstPersonHeadBoneMatrix;
        _collectModelAction = CollectAvatarModel;
        _collectHumanoidAction = CollectAvatarHumanoid;
        _collectHeadChopAction = CollectAvatarHeadChop;
    }

    /// <summary>
    /// Enables or disables first-person head hiding for the bound local avatar.
    /// </summary>
    public bool HeadHidingEnabled
    {
        get => _headHidingEnabled;
        set
        {
            if (!SetField(ref _headHidingEnabled, value))
                return;

            if (!value)
                RestoreAvatarVisibilityState();
        }
    }

    /// <summary>
    /// Binds <paramref name="avatarRoot"/> and finds its humanoid head bone and any
    /// <see cref="VrHeadChopComponent"/> overrides in the avatar hierarchy.
    /// </summary>
    public void BindAvatar(SceneNode avatarRoot)
    {
        RestoreAvatarVisibilityState();
        DestroySkinnedProxies();
        _avatarRoot = avatarRoot;
        _boneRulesBuilt = false;
        RefreshHeadBindings();
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RefreshHeadBindings();
        RegisterTick(ETickGroup.Late, (int)ETickOrder.Scene + 3, ApplyHeadChopAfterRig);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Late, (int)ETickOrder.Scene + 3, ApplyHeadChopAfterRig);
        RestoreAvatarVisibilityState();
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        UnregisterTick(ETickGroup.Late, (int)ETickOrder.Scene + 3, ApplyHeadChopAfterRig);
        RestoreAvatarVisibilityState();
        DestroySkinnedProxies();
        base.OnDestroying();
    }

    private void RefreshHeadBindings()
    {
        _headNode = null;
        _discoveredHumanoid = null;
        _headChopComponents.Clear();
        _boneRulesBuilt = false;
        if (_avatarRoot is null || _avatarRoot.IsDestroyed)
            return;

        _avatarRoot.IterateComponents(_collectHumanoidAction, true);
        _headNode = _discoveredHumanoid?.Head.Node;
        _avatarRoot.IterateComponents(_collectHeadChopAction, true);
    }

    private void CollectAvatarHumanoid(HumanoidComponent component)
        => _discoveredHumanoid ??= component;

    private void CollectAvatarHeadChop(VrHeadChopComponent component)
        => _headChopComponents.Add(component);

    private void CollectAvatarModel(ModelComponent component)
    {
        if (component.IsDestroyed || component.SceneNode is null || _proxyNodeSet.Contains(component.SceneNode))
            return;
        _discoveredModels.Add(component);
    }

    private void ApplyHeadChopAfterRig()
    {
        if (!_headHidingEnabled || _avatarRoot is null || _avatarRoot.IsDestroyed)
        {
            if (_splitApplied)
                RestoreAvatarVisibilityState();
            return;
        }

        if (_headNode is null || _headNode.IsDestroyed)
            RefreshHeadBindings();

        if (_headNode is null || _headNode.IsDestroyed || _headNode.Transform is null)
            return;

        bool isVrActive = RuntimeVrStateServices.IsOpenXRActive || RuntimeVrStateServices.IsInVR;
        bool rulesChanged = false;
        if (!_boneRulesBuilt || _lastHeadChopVrState != isVrActive || _headChopComponents.Count > 0)
        {
            rulesChanged = RebuildBoneChopRules(isVrActive);
            _lastHeadChopVrState = isVrActive;
            _boneRulesBuilt = true;
        }

        EnsureFirstPersonSplit();
        SyncSkinnedProxies(rulesChanged);
    }

    private Vector3 ResolveHeadWorldPosition()
    {
        if (_headNode?.Transform is not { } headTransform)
            return Vector3.Zero;

        Matrix4x4 renderMatrix = headTransform.RenderMatrix;
        return !renderMatrix.Equals(Matrix4x4.Identity)
            ? renderMatrix.Translation
            : headTransform.WorldMatrix.Translation;
    }

    private void EnsureFirstPersonSplit()
    {
        if (_avatarRoot is null || _avatarRoot.IsDestroyed || _headNode is null)
            return;

        _discoveredModels.Clear();
        _avatarRoot.IterateComponents(_collectModelAction, true);
        if (_discoveredModels.Count == 0)
            return;

        for (int i = 0; i < _discoveredModels.Count; ++i)
        {
            ModelComponent sourceModel = _discoveredModels[i];
            SceneNode sourceNode = sourceModel.SceneNode;
            if (HasSkinnedMesh(sourceModel))
            {
                AssignLayerAndSave(sourceNode, sourceModel, DefaultLayers.LocalVrAvatarIndex);
                SkinnedProxyEntry? proxy = FindSkinnedProxy(sourceModel);
                if (proxy is null)
                    CreateSkinnedProxy(sourceModel);
            }
            else if (IsRigidHeadAttachmentHidden(sourceNode))
            {
                AssignLayerAndSave(sourceNode, sourceModel, DefaultLayers.LocalVrAvatarIndex);
            }
            else
            {
                // Place non-head rigid avatar attachments on the Characters layer so
                // character-only mirrors render the full avatar without static world geometry.
                AssignLayerAndSave(sourceNode, sourceModel, DefaultLayers.CharactersIndex);
            }
        }

        _splitApplied = true;
    }

    private void SyncSkinnedProxies(bool refreshBoneMatrices)
    {
        for (int i = _skinnedProxies.Count - 1; i >= 0; --i)
        {
            SkinnedProxyEntry entry = _skinnedProxies[i];
            if (entry.SourceModel.IsDestroyed || entry.SourceModel.SceneNode is null ||
                entry.SourceModel.SceneNode.IsDestroyed || entry.ProxyNode.IsDestroyed)
            {
                if (!entry.ProxyNode.IsDestroyed && !entry.ProxyNode.IsDestroyQueued)
                    entry.ProxyNode.Destroy();
                _proxyNodeSet.Remove(entry.ProxyNode);
                _skinnedProxies.RemoveAt(i);
                continue;
            }

            bool shouldShowProxy = entry.SourceModel.IsActiveInHierarchy;
            if (entry.ProxyNode.IsActiveSelf != shouldShowProxy)
                entry.ProxyNode.IsActiveSelf = shouldShowProxy;

            if (!ReferenceEquals(entry.BoundModelAsset, entry.SourceModel.Model))
            {
                entry.BoundModelAsset = entry.SourceModel.Model;
                entry.ProxyModel.DefaultBlendShapeWeights = entry.SourceModel.DefaultBlendShapeWeights;
                entry.ProxyModel.Model = entry.SourceModel.Model;
                SyncProxySkinningBindings(entry.SourceModel, entry.ProxyModel);
                ApplyLayerToModel(entry.ProxyNode, entry.ProxyModel, DefaultLayers.LocalVrFirstPersonIndex);
            }

            if (!shouldShowProxy)
                continue;

            var meshes = entry.ProxyModel.Meshes;
            for (int m = 0; m < meshes.Count; ++m)
            {
                RenderableMesh renderable = meshes[m];
                if (renderable.RenderInfo.Layer != DefaultLayers.LocalVrFirstPersonIndex)
                    renderable.RenderInfo.Layer = DefaultLayers.LocalVrFirstPersonIndex;
                if (renderable.RenderInfo.CastsShadows)
                    renderable.RenderInfo.CastsShadows = false;
                if (!renderable.IsSkinned)
                    continue;

                foreach (RenderableMesh.RenderableLOD lod in renderable.LODs)
                {
                    // SetBoneMatrixModifier refreshes bone matrices once when first attached; avoid
                    // dirtying SkinnedOutputVersion every update tick so the render thread's Vulkan
                    // deformation receipt stays valid across prepass and draw submission.
                    lod.Renderer.SetBoneMatrixModifier(_boneMatrixModifier);
                    if (refreshBoneMatrices)
                        lod.Renderer.RefreshBoneMatricesFromRenderState();
                }
            }
        }
    }

    private void CreateSkinnedProxy(ModelComponent sourceModel)
    {
        SceneNode sourceNode = sourceModel.SceneNode;
        // Create the proxy directly as a child of sourceNode so it inherits World immediately
        // and resolves prefab bone references within the exact same avatar hierarchy.
        SceneNode proxyNode = sourceNode.NewChild($"{sourceNode.Name} (FirstPersonHeadChop)");
        proxyNode.Layer = DefaultLayers.LocalVrFirstPersonIndex;
        _proxyNodeSet.Add(proxyNode);
        if (proxyNode.Transform is not XREngine.Scene.Transforms.Transform)
            _ = proxyNode.SetTransform<XREngine.Scene.Transforms.Transform>();
        proxyNode.Transform.RecalculateMatrices(true);

        ModelComponent proxyModel = proxyNode.AddComponent<ModelComponent>()!;
        proxyModel.MeshCastsShadows = false;
        proxyModel.UpdateSkinningWhenOffscreen = true;
        proxyModel.UseSkinnedMotionVectors = sourceModel.UseSkinnedMotionVectors;
        proxyModel.DefaultBlendShapeWeights = sourceModel.DefaultBlendShapeWeights;
        proxyModel.Model = sourceModel.Model;
        SyncProxySkinningBindings(sourceModel, proxyModel);
        ApplyLayerToModel(proxyNode, proxyModel, DefaultLayers.LocalVrFirstPersonIndex);

        var meshes = proxyModel.Meshes;
        for (int m = 0; m < meshes.Count; ++m)
        {
            RenderableMesh renderable = meshes[m];
            if (!renderable.IsSkinned)
                continue;
            foreach (RenderableMesh.RenderableLOD lod in renderable.LODs)
                lod.Renderer.SetBoneMatrixModifier(_boneMatrixModifier);
        }

        _skinnedProxies.Add(new SkinnedProxyEntry
        {
            SourceModel = sourceModel,
            ProxyNode = proxyNode,
            ProxyModel = proxyModel,
            BoundModelAsset = sourceModel.Model,
        });
    }

    /// <summary>
    /// Copies the live skeleton bone bindings, root bone, and material override from
    /// <paramref name="sourceModel"/> onto <paramref name="proxyModel"/> so the first-person proxy
    /// always deforms with the active avatar skeleton instead of an unbound prefab bind pose.
    /// </summary>
    private static void SyncProxySkinningBindings(ModelComponent sourceModel, ModelComponent proxyModel)
    {
        var sourceMeshes = sourceModel.Meshes;
        var proxyMeshes = proxyModel.Meshes;
        int meshCount = Math.Min(sourceMeshes.Count, proxyMeshes.Count);
        for (int m = 0; m < meshCount; ++m)
            proxyMeshes[m].SyncSkinningBindingsFrom(sourceMeshes[m]);
    }

    private SkinnedProxyEntry? FindSkinnedProxy(ModelComponent sourceModel)
    {
        for (int i = 0; i < _skinnedProxies.Count; ++i)
        {
            if (ReferenceEquals(_skinnedProxies[i].SourceModel, sourceModel))
                return _skinnedProxies[i];
        }
        return null;
    }

    private static bool HasSkinnedMesh(ModelComponent modelComponent)
    {
        var meshes = modelComponent.Meshes;
        for (int i = 0; i < meshes.Count; ++i)
        {
            if (meshes[i].IsSkinned)
                return true;
        }
        return false;
    }

    private bool IsRigidHeadAttachmentHidden(SceneNode node)
    {
        if (_headNode is null)
            return false;

        bool isUnderHead = ReferenceEquals(node, _headNode) || GetDepthUnderHead(node) > 0;
        if (!isUnderHead)
            return false;

        if (node.Transform is { } transform && _boneChopRules.TryGetValue(transform, out BoneChopRule rule))
            return rule.ScaleFactor <= HiddenHeadScale + 1.0e-5f;

        return true;
    }

    private void AssignLayerAndSave(SceneNode node, ModelComponent model, int layer)
    {
        bool alreadySaved = false;
        for (int i = 0; i < _savedNodeLayers.Count; ++i)
        {
            if (ReferenceEquals(_savedNodeLayers[i].Node, node))
            {
                alreadySaved = true;
                break;
            }
        }

        if (!alreadySaved)
            _savedNodeLayers.Add(new SavedNodeLayer(node, model, node.Layer));

        ApplyLayerToModel(node, model, layer);
    }

    private static void ApplyLayerToModel(SceneNode node, ModelComponent model, int layer)
    {
        if (node.Layer != layer)
            node.Layer = layer;

        var meshes = model.Meshes;
        for (int i = 0; i < meshes.Count; ++i)
        {
            if (meshes[i].RenderInfo.Layer != layer)
                meshes[i].RenderInfo.Layer = layer;
        }
    }

    private void RestoreAvatarVisibilityState()
    {
        for (int i = _savedNodeLayers.Count - 1; i >= 0; --i)
        {
            SavedNodeLayer saved = _savedNodeLayers[i];
            if (!saved.Node.IsDestroyed && !saved.Model.IsDestroyed)
                ApplyLayerToModel(saved.Node, saved.Model, saved.OriginalLayer);
        }
        _savedNodeLayers.Clear();

        for (int i = 0; i < _skinnedProxies.Count; ++i)
        {
            SceneNode proxyNode = _skinnedProxies[i].ProxyNode;
            if (!proxyNode.IsDestroyed && !proxyNode.IsDestroyQueued && proxyNode.IsActiveSelf)
                proxyNode.IsActiveSelf = false;
        }

        _splitApplied = false;
    }

    private void DestroySkinnedProxies()
    {
        for (int i = _skinnedProxies.Count - 1; i >= 0; --i)
        {
            SceneNode proxyNode = _skinnedProxies[i].ProxyNode;
            if (!proxyNode.IsDestroyed && !proxyNode.IsDestroyQueued)
                proxyNode.Destroy();
        }
        _skinnedProxies.Clear();
        _proxyNodeSet.Clear();
    }

    private Matrix4x4 TransformFirstPersonHeadBoneMatrix(TransformBase bone, Matrix4x4 worldMatrix)
    {
        if (!_boneChopRules.TryGetValue(bone, out BoneChopRule rule))
            return worldMatrix;

        float scale = rule.ScaleFactor;
        if (MathF.Abs(scale - 1.0f) <= 1.0e-5f && rule.KeepWorldOffset)
            return worldMatrix;

        Matrix4x4 result = worldMatrix;
        result.M11 *= scale;
        result.M12 *= scale;
        result.M13 *= scale;
        result.M21 *= scale;
        result.M22 *= scale;
        result.M23 *= scale;
        result.M31 *= scale;
        result.M32 *= scale;
        result.M33 *= scale;

        if (!rule.KeepWorldOffset)
        {
            Vector3 head = ResolveHeadWorldPosition();
            result.M41 = head.X + (worldMatrix.M41 - head.X) * scale;
            result.M42 = head.Y + (worldMatrix.M42 - head.Y) * scale;
            result.M43 = head.Z + (worldMatrix.M43 - head.Z) * scale;
        }

        return result;
    }

    private bool RebuildBoneChopRules(bool isVrActive)
    {
        int previousCount = _boneChopRules.Count;
        if (_headNode is null || _headNode.IsDestroyed)
        {
            _boneChopRules.Clear();
            return previousCount > 0;
        }

        CollectHeadChopTargets(isVrActive);
        bool changed = false;
        int visitedCount = 0;
        PopulateHeadSubtreeRules(_headNode, HiddenHeadScale, keepWorldOffset: false, ref changed, ref visitedCount);
        if (visitedCount != previousCount)
        {
            _boneChopRules.Clear();
            visitedCount = 0;
            PopulateHeadSubtreeRules(_headNode, HiddenHeadScale, keepWorldOffset: false, ref changed, ref visitedCount);
            changed = true;
        }
        return changed;
    }

    private void PopulateHeadSubtreeRules(
        SceneNode node,
        float inheritedScale,
        bool keepWorldOffset,
        ref bool changed,
        ref int visitedCount)
    {
        float effectiveScale = inheritedScale;
        bool effectiveKeepOffset = keepWorldOffset;

        for (int i = 0; i < _overrideTargets.Count; ++i)
        {
            if (!ReferenceEquals(_overrideTargets[i].Node, node))
                continue;

            effectiveScale = HiddenHeadScale + (1.0f - HiddenHeadScale) * _overrideTargets[i].ScaleFactor;
            effectiveKeepOffset = _overrideTargets[i].ScaleFactor > 0.0f;
            break;
        }

        if (node.Transform is { } transform)
        {
            BoneChopRule nextRule = new(effectiveScale, effectiveKeepOffset);
            if (!_boneChopRules.TryGetValue(transform, out BoneChopRule existingRule) || existingRule != nextRule)
            {
                _boneChopRules[transform] = nextRule;
                changed = true;
            }
            ++visitedCount;
            var children = transform.Children;
            for (int i = 0; i < children.Count; ++i)
            {
                SceneNode? child = children[i]?.SceneNode;
                if (child is not null && !_proxyNodeSet.Contains(child))
                    PopulateHeadSubtreeRules(child, effectiveScale, effectiveKeepOffset, ref changed, ref visitedCount);
            }
        }
    }

    private void CollectHeadChopTargets(bool isVrActive)
    {
        _overrideTargets.Clear();
        if (_headNode is null || _headChopComponents.Count == 0)
            return;

        for (int c = 0; c < _headChopComponents.Count; ++c)
        {
            VrHeadChopComponent component = _headChopComponents[c];
            if (component.IsDestroyed || !component.IsActiveInHierarchy || component.GlobalScaleFactor <= 0.0f)
                continue;

            List<VrHeadChopTargetBone> entries = component.TargetBones;
            for (int e = 0; e < entries.Count; ++e)
            {
                VrHeadChopTargetBone entry = entries[e];
                SceneNode? targetNode = entry?.Target;
                if (entry is null || targetNode is null || targetNode.IsDestroyed || !entry.MatchesCondition(isVrActive))
                    continue;

                int depth = GetDepthUnderHead(targetNode);
                if (depth <= 0)
                    continue;

                float effectiveScale = Math.Clamp(entry.ScaleFactor * component.GlobalScaleFactor, 0.0f, 1.0f);
                UpsertOverrideTarget(targetNode, depth, effectiveScale);
            }
        }
    }

    private void UpsertOverrideTarget(SceneNode node, int depth, float scaleFactor)
    {
        for (int i = 0; i < _overrideTargets.Count; ++i)
        {
            if (!ReferenceEquals(_overrideTargets[i].Node, node))
                continue;

            if (scaleFactor > _overrideTargets[i].ScaleFactor)
                _overrideTargets[i] = new OverrideTarget(node, depth, scaleFactor);
            return;
        }

        _overrideTargets.Add(new OverrideTarget(node, depth, scaleFactor));
    }

    private int GetDepthUnderHead(SceneNode node)
    {
        if (_headNode is null || ReferenceEquals(node, _headNode))
            return 0;

        int depth = 0;
        for (SceneNode? current = node.Parent; current is not null; current = current.Parent)
        {
            ++depth;
            if (ReferenceEquals(current, _headNode))
                return depth;
        }

        return 0;
    }
}
