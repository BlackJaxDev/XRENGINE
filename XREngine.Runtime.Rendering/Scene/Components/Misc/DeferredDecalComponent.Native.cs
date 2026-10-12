using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Data.Core;

namespace XREngine.Components;

public partial class DeferredDecalComponent
{
    private IRuntimeRenderWorld? _nativeDecalWorld;
    private ObjectCacheOwnership? _decalBoxOwnership;
    private XRMeshRenderer? _decalBoxRenderer;
    private XRMesh? _decalBoxMesh;
    private long _decalBoxGeometryRevision;
    private XRMeshRenderer.DelSetUniforms? _decalUniforms;
    private bool _decalTeardownRequested;

    private void RebindDecalRenderer()
    {
        if (_decalTeardownRequested) return;
        ReleaseOwnedDecalBox();
        RenderCommandDecal.RenderPass = UseForwardOit
            ? (int)EDefaultRenderPass.WeightedBlendedOitForward : (int)EDefaultRenderPass.DeferredDecals;
        if (Material is null) return;
        // The material and projected image predate this scope and remain borrowed.
        // The box, its buffers and renderer constructor storage share one owner.
        using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
        XRMeshRenderer renderer = new(XRMesh.Shapes.SolidBox(-Vector3.One, Vector3.One), Material);
        if (_decalUniforms is null) SetField(ref _decalUniforms, DecalManager_SettingUniforms, publishNotifications: false);
        renderer.SettingUniforms += _decalUniforms;
        SetField(ref _decalBoxOwnership, publication.CompleteWithOwnership(), publishNotifications: false);
        SetField(ref _decalBoxRenderer, renderer, publishNotifications: false);
        SetField(ref _decalBoxMesh, renderer.Mesh, publishNotifications: false);
        SetField(ref _decalBoxGeometryRevision, renderer.Mesh!.GeometryRevision, publishNotifications: false);
        RenderCommandDecal.Mesh = renderer;
    }

    private void ReleaseOwnedDecalBox()
    {
        RenderCommandDecal.Mesh = null;
        if (_decalBoxRenderer is { } renderer) renderer.SettingUniforms -= _decalUniforms;
        // Failed members stay owned for a later cleanup retry.
        _decalBoxOwnership?.Dispose();
        SetField(ref _decalBoxOwnership, null, publishNotifications: false);
        SetField(ref _decalBoxRenderer, null, publishNotifications: false);
        SetField(ref _decalBoxMesh, null, publishNotifications: false);
    }

    /// <summary>Checks the actual draw route before admitting the component's exact projection.</summary>
    public bool HasDefaultDecalDrawContract(bool allowUnboundRenderer = false)
    {
        var command = RenderCommandDecal;
        if (command.MaterialOverride is not null || command.RenderOptionsOverride is not null ||
            command.Instances != 1 || !command.WorldMatrixIsModelMatrix ||
            !RenderInfo.HasDefaultCommandCallbacks(command, World.GetRenderWorld()?.VisualScene.RenderableSwapHandler) ||
            command.RenderPass != (int)(UseForwardOit ? EDefaultRenderPass.WeightedBlendedOitForward : EDefaultRenderPass.DeferredDecals) ||
            RenderInfo.RenderCommands.Count != 1 || !ReferenceEquals(RenderInfo.RenderCommands[0], command) ||
            RenderInfo.PreCollectCommandsCallback is not null || RenderInfo.CullingIntersectionOverride is not null)
            return false;
        if (SceneNode is not null && command.WorldMatrix != Matrix4x4.CreateScale(HalfExtents) * Transform.RenderMatrix)
            return false;
        if (command.Mesh is null) return allowUnboundRenderer && _decalBoxRenderer is null;
        return ReferenceEquals(command.Mesh, _decalBoxRenderer) && _decalBoxRenderer is { IsDestroyed: false } renderer &&
            ReferenceEquals(renderer.Material, Material) && ReferenceEquals(renderer.Mesh, _decalBoxMesh) &&
            _decalBoxMesh is { IsDestroyed: false } mesh && mesh.GeometryRevision == _decalBoxGeometryRevision &&
            renderer.Submeshes.Count == 0 && renderer.BindingPublishers.Count == 0 && !renderer.HasRenderDataPreparation &&
            _decalUniforms is not null && renderer.HasOnlySettingUniformsHandler(_decalUniforms);
    }

    private void RefreshNativeDecalRegistration()
    {
        IRuntimeRenderWorld? world = !_decalTeardownRequested && IsActiveInHierarchy ? World.GetRenderWorld() : null;
        if (ReferenceEquals(world, _nativeDecalWorld)) return;
        UnregisterNativeDecal();
        if (world is null) return;
        AdvancedAuthoredDecalRegistry.Register(world, this);
        SetField(ref _nativeDecalWorld, world, publishNotifications: false);
    }

    private void UnregisterNativeDecal()
    {
        if (_nativeDecalWorld is not { } world) return;
        AdvancedAuthoredDecalRegistry.Unregister(world, this);
        SetField(ref _nativeDecalWorld, null, publishNotifications: false);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterNativeDecal();
        base.OnComponentDeactivated();
        ReleaseOwnedDecalBox();
    }

    protected override void OnDestroying()
    {
        SetField(ref _decalTeardownRequested, true, publishNotifications: false);
        List<Exception>? failures = null;
        try { UnregisterNativeDecal(); }
        catch (Exception error) { (failures ??= []).Add(error); }
        try { base.OnDestroying(); }
        catch (Exception error) { (failures ??= []).Add(error); }
        try { RenderInfo.Dispose(); }
        catch (Exception error) { (failures ??= []).Add(error); }
        try { DebugRenderInfo.Dispose(); }
        catch (Exception error) { (failures ??= []).Add(error); }
        if (RenderInfo.Owner is null && DebugRenderInfo.Owner is null)
            try { ReleaseOwnedDecalBox(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        if (failures is not null)
            throw new AggregateException("Authored decal teardown retains failed owned resources for retry.", failures);
    }
}
