using System.Runtime.CompilerServices;
using XREngine.Scene;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>
/// Owns DDGI aggregate geometry for one physical render-pipeline instance.
/// GPU wrappers are renderer-owner specific, so a scene cannot share its
/// aggregate BVH or material atlas across pipeline instances.
/// </summary>
internal sealed class DDGIGeometryResources
{
    private static readonly ConditionalWeakTable<XRRenderPipelineInstance, DDGIGeometryResources> Resources = new();

    private VisualScene3D? _scene;
    private IRenderApiWrapperOwner? _apiWrapperIdentityOwner;
    private GpuDdgiGeometryService? _geometry;

    private DDGIGeometryResources(XRRenderPipelineInstance owner)
        => owner.CacheClearing += Clear;

    public static GpuDdgiGeometryService GetOrCreate(XRRenderPipelineInstance owner, VisualScene3D scene)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(scene);
        return Resources.GetValue(owner, static pipeline => new DDGIGeometryResources(pipeline))
            .GetOrCreate(scene);
    }

    private GpuDdgiGeometryService GetOrCreate(VisualScene3D scene)
    {
        IRenderApiWrapperOwner? owner = AbstractRenderer.Current?.ApiWrapperIdentityOwner;
        if (!ReferenceEquals(_scene, scene) || !ReferenceEquals(_apiWrapperIdentityOwner, owner))
        {
            // Aggregate geometry and material images are generated on the GPU;
            // a new renderer cannot rehydrate them from unchanged logical handles.
            Clear();
            _scene = scene;
            _apiWrapperIdentityOwner = owner;
        }

        return _geometry ??= new GpuDdgiGeometryService();
    }

    private void Clear()
    {
        _geometry?.Dispose();
        _geometry = null;
        _scene = null;
        _apiWrapperIdentityOwner = null;
    }
}
