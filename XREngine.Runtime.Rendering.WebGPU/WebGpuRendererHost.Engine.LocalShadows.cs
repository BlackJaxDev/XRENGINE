using System.Numerics;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private XRTextureCube? _defaultPointShadow;
    private ObjectCacheOwnership? _defaultPointShadowOwnership;
    private readonly int[] _defaultPointShadowClears = new int[PointLightComponent.ShadowFaceCount];
    private uint _defaultPointShadowClearFrame;
    private bool _defaultPointShadowInitialized;
    private XRTexture2D? _defaultSpotShadow;
    private ObjectCacheOwnership? _defaultSpotShadowOwnership;
    private int _defaultSpotShadowClear;
    private uint _defaultSpotShadowClearFrame;
    private bool _defaultSpotShadowInitialized;

    private bool ValidateLocalShadowProfile(IRuntimeRenderWorld world)
    {
        int points = 0, spots = 0;
        for (int i = 0; i < world.Lights.DynamicPointLights.Count; i++)
        {
            PointLightComponent light = world.Lights.DynamicPointLights[i];
            if (!light.CastsShadows) continue;
            if (++points > 1) throw ShadowUnsupported("the local receiver supports one standalone point shadow cube");
            ValidatePointShadowLight(light);
        }
        for (int i = 0; i < world.Lights.DynamicSpotLights.Count; i++)
        {
            SpotLightComponent light = world.Lights.DynamicSpotLights[i];
            if (!light.CastsShadows) continue;
            if (++spots > 1) throw ShadowUnsupported("the local receiver supports one standalone spot shadow map");
            ValidateSpotShadowLight(light);
        }
        if (points > 0)
        {
            EngineMaterialVariantKey caster = new(EngineMaterialSemanticIdentity.OpaquePointShadowDepthV1,
                ShaderCompileTarget.WebGPUWgsl, "point-shadow-depth", "static-position-v1", "radial-r16f-v1");
            if (_materialVariants?.TryResolve(caster, out _) != true)
                throw ShadowUnsupported("the package must supply the exact radial point-shadow caster");
        }
        if (spots > 0)
        {
            EngineMaterialVariantKey caster = new(EngineMaterialSemanticIdentity.OpaqueSpotShadowDepthV1,
                ShaderCompileTarget.WebGPUWgsl, "spot-shadow-depth", "static-position-v1", "projected-r16f-v1");
            if (_materialVariants?.TryResolve(caster, out _) != true)
                throw ShadowUnsupported("the package must supply the exact projected spot-shadow caster");
        }
        return points != 0 || spots != 0;
    }

    private static void ValidateSpotShadowLight(SpotLightComponent light)
    {
        int qualityLimit = RuntimeEngine.Rendering.Settings.BrowserWebGpuQuality.MaxSpotShadowDimension;
        (uint width, uint height) = light.GetEffectiveShadowMapResolution(
            light.ShadowMapResolutionWidth, light.ShadowMapResolutionHeight);
        if (width > qualityLimit || height > qualityLimit)
            throw ShadowUnsupported($"spot '{light.Name}' shadow target exceeds the selected browser spot-shadow limit {qualityLimit}");
        light.ValidateCookedShadowConfiguration();
        if (light.ShadowCamera is not { DepthMode: XRCamera.EDepthMode.Normal, Parameters: XRPerspectiveCameraParameters })
            throw ShadowUnsupported($"spot '{light.Name}' requires its normal-Z perspective shadow camera");
    }

    private static void ValidatePointShadowLight(PointLightComponent light)
    {
        int qualityLimit = RuntimeEngine.Rendering.Settings.BrowserWebGpuQuality.MaxPointShadowDimension;
        (uint width, uint height) = light.GetEffectiveShadowMapResolution(
            light.ShadowMapResolutionWidth, light.ShadowMapResolutionHeight);
        if (width > qualityLimit || height > qualityLimit)
            throw ShadowUnsupported($"point '{light.Name}' shadow target exceeds the selected browser point-shadow limit {qualityLimit}");
        light.ValidateCookedShadowConfiguration();
        for (int face = 0; face < PointLightComponent.ShadowFaceCount; face++)
            if (!light.TryGetShadowFaceCamera(face, out XRCamera camera) ||
                camera.DepthMode != XRCamera.EDepthMode.Normal || camera.Parameters is not XRPerspectiveCameraParameters)
                throw ShadowUnsupported($"point '{light.Name}' requires all six normal-Z perspective shadow cameras");
    }

    private void PublishLocalShadows(WebGpuRenderProgram program, IRuntimeRenderWorld world)
    {
        SpotLightComponent? spot = null;
        PointLightComponent? point = null;
        int spotIndex = 0, pointIndex = 0;
        for (int i = 0; i < world.Lights.DynamicSpotLights.Count; i++)
            if (world.Lights.DynamicSpotLights[i].CastsShadows && world.Lights.DynamicSpotLights[i].ShadowFrustumRelevant)
            { spot = world.Lights.DynamicSpotLights[i]; spotIndex = i; }
        for (int i = 0; i < world.Lights.DynamicPointLights.Count; i++)
            if (world.Lights.DynamicPointLights[i].CastsShadows)
            { point = world.Lights.DynamicPointLights[i]; pointIndex = i; }

        XRTexture2D spotTexture = spot?.CookedShadowReceiverTexture ?? EnsureDefaultSpotShadow();
        if (spot is not null)
        {
            if (spot.CookedShadowReceiverTexture is null)
                throw ShadowUnsupported($"spot '{spot.Name}' has no light-owned projected depth texture");
            WebGpuTexture2D api = (WebGpuTexture2D)GetOrCreateAPIRenderObject(spotTexture, generateNow: true)!;
            if (!api.WasProducedInFrame(_engineFrameSequence) && !CanPublishReusedShadow(spot, spotTexture))
                MarkEngineDrawPending();
        }
        program.SetMatrix("SpotShadowViewProjection", spot?.ShadowCamera?.ViewProjectionMatrix ?? Matrix4x4.Identity);
        program.SetVector4("SpotShadowControl", spot is null ? Vector4.Zero :
            new Vector4(1, spotIndex, spot.ShadowCamera!.NearZ, spot.ShadowCamera.FarZ));
        program.SetVector4("SpotShadowPosition", spot is null ? Vector4.Zero : new Vector4(spot.Transform.RenderTranslation, 0));
        program.SetVector4("SpotShadowDirection", spot is null ? Vector4.Zero : new Vector4(spot.Transform.RenderForward, spot.OuterCutoff));
        program.SetVector4("SpotShadowBias", spot?.ShadowBiasParameters ?? Vector4.Zero);
        program.SetVector4("SpotShadowFilter", FilterParameters(spot));
        program.SetVector4("SpotShadowSource", new Vector4(spot?.EffectiveLightSourceRadius ?? 0, 0, 0, 0));
        program.Data.Sampler("SpotShadowMap", spotTexture, 0);

        XRTextureCube pointTexture;
        if (point is null) pointTexture = EnsureDefaultPointShadow();
        else
        {
            pointTexture = point.CookedShadowReceiverTexture
                ?? throw ShadowUnsupported($"point '{point.Name}' has no light-owned radial-distance cube");
            WebGpuTextureCube api = (WebGpuTextureCube)GetOrCreateAPIRenderObject(pointTexture, generateNow: true)!;
            if ((!api.WasProducedInFrame(_engineFrameSequence) && !CanPublishReusedShadow(point, pointTexture)) ||
                point.LastRenderedShadowFaceMask != 63)
                MarkEngineDrawPending();
        }
        program.SetVector4("PointShadowControl", point is null ? Vector4.Zero :
            new Vector4(1, pointIndex, point.ShadowNearPlaneDistance, point.Radius));
        program.SetVector4("PointShadowPosition", point is null ? Vector4.Zero : new Vector4(point.Transform.RenderTranslation, point.Radius));
        program.SetVector4("PointShadowBias", point?.ShadowBiasParameters ?? Vector4.Zero);
        program.SetVector4("PointShadowFilter", FilterParameters(point));
        program.SetVector4("PointShadowSource", new Vector4(point?.EffectiveLightSourceRadius ?? 0, 0, 0, 0));
        program.Data.Sampler("PointShadowMap", pointTexture, 0);
    }

    private static Vector4 FilterParameters(LightComponent? light)
        => light is null ? Vector4.Zero : new(light.BlockerSearchRadius, light.FilterRadius, light.MinPenumbra, light.MaxPenumbra);

    private XRTexture2D EnsureDefaultSpotShadow()
    {
        if (_defaultSpotShadow is null)
        {
            using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
            using IDisposable suppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
            XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(1, 1, EPixelInternalFormat.R16f,
                EPixelFormat.Red, EPixelType.HalfFloat, EFrameBufferAttachment.ColorAttachment0);
            texture.Name = "Disabled spot shadow projected depth";
            texture.SizedInternalFormat = ESizedInternalFormat.R16f;
            texture.Resizable = false;
            texture.AutoGenerateMipmaps = false;
            texture.MinFilter = ETexMinFilter.Nearest;
            texture.MagFilter = ETexMagFilter.Nearest;
            texture.UWrap = texture.VWrap = ETexWrapMode.ClampToEdge;
            texture.LargestMipmapLevel = texture.SmallestAllowedMipmapLevel = 0;
            texture.MaxAnisotropy = 1;
            SetField(ref _defaultSpotShadowOwnership, publication.CompleteWithOwnership());
            SetField(ref _defaultSpotShadow, texture);
        }
        if (!_defaultSpotShadowInitialized && _defaultSpotShadowClearFrame != _engineFrameSequence)
        {
            if (_defaultSpotShadowClear == 0)
            {
                WebGpuTexture2D api = (WebGpuTexture2D)GetOrCreateAPIRenderObject(_defaultSpotShadow, generateNow: true)!;
                BrowserFrameBufferPlan plan = new([new BrowserColorAttachmentPlan(api.GetRenderView(0, -1), true, true, Vector4.One)]);
                SetField(ref _defaultSpotShadowClear, PrepareCommands(
                    "{\"label\":\"Disabled spot shadow initialization\",\"commands\":[{\"type\":\"clear\",\"pass\":" + plan.ToJson() + "}]}"));
            }
            RecordEngineCommands(_defaultSpotShadowClear, []);
            SetField(ref _defaultSpotShadowClearFrame, _engineFrameSequence, publishNotifications: false);
        }
        return _defaultSpotShadow ?? throw new InvalidOperationException("WebGPU.Shadow.DefaultSpotMissing: initialization did not publish its owned texture.");
    }

    private XRTextureCube EnsureDefaultPointShadow()
    {
        if (_defaultPointShadow is null)
        {
            using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
            using IDisposable suppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
            XRTextureCube texture = new(1, EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, false)
            {
                Name = "Disabled point shadow radial distance",
                Resizable = false,
                AutoGenerateMipmaps = false,
                MinFilter = ETexMinFilter.Nearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                WWrap = ETexWrapMode.ClampToEdge,
                LargestMipmapLevel = 0,
                SmallestAllowedMipmapLevel = 0,
            };
            SetField(ref _defaultPointShadowOwnership, publication.CompleteWithOwnership());
            SetField(ref _defaultPointShadow, texture);
        }
        if (!_defaultPointShadowInitialized && _defaultPointShadowClearFrame != _engineFrameSequence)
        {
            for (int face = 0; face < _defaultPointShadowClears.Length; face++)
            {
                if (_defaultPointShadowClears[face] == 0)
                {
                    WebGpuTextureCube api = (WebGpuTextureCube)GetOrCreateAPIRenderObject(_defaultPointShadow, generateNow: true)!;
                    BrowserFrameBufferPlan plan = new([new BrowserColorAttachmentPlan(api.GetRenderView(0, face), true, true, Vector4.One)]);
                    // Every ordered engine frame record references one retained operation.
                    _defaultPointShadowClears[face] = PrepareCommands(
                        "{\"label\":\"Disabled point shadow face initialization\",\"commands\":[{\"type\":\"clear\",\"pass\":" + plan.ToJson() + "}]}");
                }
                RecordEngineCommands(_defaultPointShadowClears[face], []);
            }
            SetField(ref _defaultPointShadowClearFrame, _engineFrameSequence, publishNotifications: false);
        }
        return _defaultPointShadow ?? throw new InvalidOperationException("WebGPU.Shadow.DefaultPointMissing: initialization did not publish its owned texture.");
    }

    private void CommitLocalShadowDefaults()
    {
        if (_defaultPointShadowClearFrame == _engineFrameSequence)
            SetField(ref _defaultPointShadowInitialized, true, publishNotifications: false);
        if (_defaultSpotShadowClearFrame == _engineFrameSequence)
            SetField(ref _defaultSpotShadowInitialized, true, publishNotifications: false);
    }

    private void DestroyLocalShadowDefaults()
    {
        _defaultSpotShadowOwnership?.Dispose();
        SetField(ref _defaultSpotShadowOwnership, null);
        SetField(ref _defaultSpotShadow, null);
        SetField(ref _defaultSpotShadowClear, 0);
        SetField(ref _defaultSpotShadowClearFrame, 0u);
        SetField(ref _defaultSpotShadowInitialized, false);
        _defaultPointShadowOwnership?.Dispose();
        SetField(ref _defaultPointShadowOwnership, null);
        SetField(ref _defaultPointShadow, null);
        Array.Clear(_defaultPointShadowClears);
        SetField(ref _defaultPointShadowClearFrame, 0u);
        SetField(ref _defaultPointShadowInitialized, false);
    }
}
