using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Transforms;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Assigns the local avatar to a camera-selective layer while the VR rig is active.
/// Imported renderers retain the override for meshes added after model load.
/// </summary>
public sealed class VrLocalAvatarVisibilityComponent : XRComponent
{
    private readonly Dictionary<RenderableComponent, int> _originalLayers = [];
    private readonly Dictionary<RenderableMesh, int> _originalMeshLayers = [];
    private SceneNode? _avatarRoot;

    public void BindAvatar(SceneNode avatarRoot)
    {
        RestoreLayers();
        _avatarRoot = avatarRoot;
        ApplyLayer();
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        ApplyLayer();
    }

    protected override void OnComponentDeactivated()
    {
        RestoreLayers();
        base.OnComponentDeactivated();
    }

    private void ApplyLayer()
    {
        _avatarRoot?.IterateComponents<RenderableComponent>(renderable =>
        {
            if (_originalLayers.TryAdd(renderable, renderable.MeshLayer))
            {
                foreach (var mesh in renderable.Meshes)
                    _originalMeshLayers.TryAdd(mesh, mesh.RenderInfo.Layer);
                renderable.MeshLayer = DefaultLayers.LocalVrAvatarIndex;
            }
        }, true);
    }

    private void RestoreLayers()
    {
        foreach (var (renderable, originalLayer) in _originalLayers)
        {
            renderable.MeshLayer = originalLayer;
            foreach (var mesh in renderable.Meshes)
            {
                if (!_originalMeshLayers.ContainsKey(mesh))
                    mesh.RenderInfo.Layer = originalLayer >= 0 ? originalLayer : DefaultLayers.DynamicIndex;
            }
        }
        foreach (var (mesh, originalLayer) in _originalMeshLayers)
            mesh.RenderInfo.Layer = originalLayer;
        _originalLayers.Clear();
        _originalMeshLayers.Clear();
    }
}
