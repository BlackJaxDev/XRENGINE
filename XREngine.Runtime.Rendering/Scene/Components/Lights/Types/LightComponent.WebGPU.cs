using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Components.Capture.Lights.Types;

public abstract partial class LightComponent
{
    private ObjectCacheOwnership? _cookedLocalShadowOwnership;
    private XRMaterialFrameBuffer? _ownedCookedLocalShadowMap;

    private bool UsesCookedLocalShadowResources
        => RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked &&
            this is PointLightComponent or SpotLightComponent;

    // The light owns the complete target generation, including its source-free
    // material and textures. A framebuffer alone does not own its attachments.
    private void CreateCookedLocalShadowResources(uint width, uint height)
    {
        using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
        using IDisposable suppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        XRMaterialFrameBuffer target = new(GetShadowMapMaterial(width, height))
        {
            Name = $"{GetType().Name}.{ID:N}.ShadowMapFbo",
        };
        ShadowMap = target;
        SetField(ref _ownedCookedLocalShadowMap, target, publishNotifications: false);
        SetField(ref _cookedLocalShadowOwnership, publication.CompleteWithOwnership(), publishNotifications: false);
    }

    private void ReleaseCookedLocalShadowResources(XRMaterialFrameBuffer? previous)
    {
        if (previous is null || !ReferenceEquals(previous, _ownedCookedLocalShadowMap) || ReferenceEquals(previous, ShadowMap))
            return;
        ObjectCacheOwnership? ownership = _cookedLocalShadowOwnership;
        SetField(ref _ownedCookedLocalShadowMap, null, publishNotifications: false);
        SetField(ref _cookedLocalShadowOwnership, null, publishNotifications: false);
        ownership?.Dispose();
    }
}
