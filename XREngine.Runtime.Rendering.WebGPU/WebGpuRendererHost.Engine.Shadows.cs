using System.Numerics;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost : IBrowserShadowReuseCapability
{
    private LightComponent? _authorizedDirectionalShadowLight;
    private LightComponent? _authorizedSpotShadowLight;
    private LightComponent? _authorizedPointShadowLight;
    private XRTexture? _authorizedDirectionalShadowTexture;
    private XRTexture? _authorizedSpotShadowTexture;
    private XRTexture? _authorizedPointShadowTexture;

    public void AuthorizeShadowReuse(LightComponent light, XRTexture texture)
    {
        switch (light)
        {
            case DirectionalLightComponent:
                _authorizedDirectionalShadowLight = light;
                _authorizedDirectionalShadowTexture = texture;
                break;
            case SpotLightComponent:
                _authorizedSpotShadowLight = light;
                _authorizedSpotShadowTexture = texture;
                break;
            case PointLightComponent:
                _authorizedPointShadowLight = light;
                _authorizedPointShadowTexture = texture;
                break;
        }
    }

    private bool CanPublishReusedShadow(LightComponent light, XRTexture texture)
        => light switch
        {
            DirectionalLightComponent => ReferenceEquals(_authorizedDirectionalShadowLight, light) &&
                ReferenceEquals(_authorizedDirectionalShadowTexture, texture),
            SpotLightComponent => ReferenceEquals(_authorizedSpotShadowLight, light) &&
                ReferenceEquals(_authorizedSpotShadowTexture, texture),
            PointLightComponent => ReferenceEquals(_authorizedPointShadowLight, light) &&
                ReferenceEquals(_authorizedPointShadowTexture, texture),
            _ => false,
        } && CanReuseCommittedShadow(texture);

    private void ResetAuthorizedShadowReuse()
    {
        _authorizedDirectionalShadowLight = null;
        _authorizedSpotShadowLight = null;
        _authorizedPointShadowLight = null;
        _authorizedDirectionalShadowTexture = null;
        _authorizedSpotShadowTexture = null;
        _authorizedPointShadowTexture = null;
    }

    public bool CanReuseCommittedShadow(XRTexture texture)
    {
        if (State != BrowserRendererState.Ready ||
            !TryGetAPIRenderObject(texture, out AbstractRenderAPIObject? api))
            return false;
        return api switch
        {
            WebGpuTexture2D image => image.HasCommittedProduction,
            WebGpuTextureCube image => image.HasCommittedProduction,
            _ => false,
        };
    }

    public bool WasShadowProducedInCurrentFrame(XRTexture texture)
    {
        if (!_engineRecording || !TryGetAPIRenderObject(texture, out AbstractRenderAPIObject? api))
            return false;
        return api switch
        {
            WebGpuTexture2D image => image.WasProducedInFrame(_engineFrameSequence),
            WebGpuTextureCube image => image.WasProducedInFrame(_engineFrameSequence),
            _ => false,
        };
    }

    public ulong GetShadowProductionTicket(XRTexture texture)
    {
        if (!TryGetAPIRenderObject(texture, out AbstractRenderAPIObject? api))
            return 0;
        return api switch
        {
            WebGpuTexture2D image => image.ProductionTicket,
            WebGpuTextureCube image => image.ProductionTicket,
            _ => 0,
        };
    }
    private XRTexture2D? _defaultDirectionalShadow;
    private ObjectCacheOwnership? _defaultDirectionalShadowOwnership;
    private int _defaultDirectionalShadowClear;
    private uint _defaultDirectionalShadowClearFrame;
    private bool _defaultDirectionalShadowInitialized;

    private void ValidateDirectionalShadowProfile(IRuntimeRenderWorld world)
    {
        int count = 0;
        for (int i = 0; i < world.Lights.DynamicDirectionalLights.Count; i++)
        {
            DirectionalLightComponent light = world.Lights.DynamicDirectionalLights[i];
            if (!light.CastsShadows) continue;
            if (++count > 1)
                throw ShadowUnsupported("the installed receiver profile supports one standalone directional shadow map");
            ValidateDirectionalShadowLight(light);
        }
        bool local = ValidateLocalShadowProfile(world);
        if (count == 0 && !local) return;
        EngineMaterialVariantKey depth = new(EngineMaterialSemanticIdentity.OpaqueShadowDepthV1,
            ShaderCompileTarget.WebGPUWgsl, "depth", "static-position-v1", "depth-normal-v1");
        EngineMaterialVariantKey receiver = new(EngineMaterialSemanticIdentity.StandardLitColorV1,
            ShaderCompileTarget.WebGPUWgsl, "opaque-forward", "static-position-normal-v1", local ? "linear-hdr-local-shadows-v1" : "linear-hdr-directional-shadow-v1");
        EngineMaterialVariantKey coverageReceiver = new(EngineMaterialSemanticIdentity.StandardLitColorV2,
            ShaderCompileTarget.WebGPUWgsl, "forward-coverage", "static-position-normal-v1", local ? "linear-hdr-local-shadows-v1" : "linear-hdr-directional-shadow-v1");
        EngineMaterialVariantKey texturedReceiver = new(EngineMaterialSemanticIdentity.StandardLitTextureV1,
            ShaderCompileTarget.WebGPUWgsl, "opaque-forward", "position-normal-uv-v1", local ? "linear-hdr-local-shadows-v1" : "linear-hdr-directional-shadow-v1");
        EngineMaterialVariantKey tangentTexturedReceiver = new(EngineMaterialSemanticIdentity.StandardLitTextureV1,
            ShaderCompileTarget.WebGPUWgsl, "opaque-forward", "position-normal-tangent-uv-v1", local ? "linear-hdr-local-shadows-v1" : "linear-hdr-directional-shadow-v1");
        if (_materialVariants is null || count > 0 && !_materialVariants.TryResolve(depth, out _) ||
            (_materialVariants.TryResolve(receiver, out _) != true && _materialVariants.TryResolve(coverageReceiver, out _) != true &&
             _materialVariants.TryResolve(texturedReceiver, out _) != true && _materialVariants.TryResolve(tangentTexturedReceiver, out _) != true))
            throw ShadowUnsupported("the package must supply exact caster and selected local/directional receiver variants");
    }

    private static void ValidateDirectionalShadowLight(DirectionalLightComponent light)
    {
        int qualityLimit = RuntimeEngine.Rendering.Settings.BrowserWebGpuQuality.MaxDirectionalShadowDimension;
        (uint width, uint height) = light.GetEffectiveShadowMapResolution(
            light.ShadowMapResolutionWidth, light.ShadowMapResolutionHeight);
        if (width > qualityLimit || height > qualityLimit)
            throw ShadowUnsupported($"light '{light.Name}' shadow target exceeds the selected browser directional-shadow limit {qualityLimit}");
        if (light.UseShadowAtlas || light.EnableCascadedShadows || light.ShadowMapEncoding != EShadowMapEncoding.Depth ||
            light.EnableContactShadows || light.SoftShadowMode != ESoftShadowMode.ContactHardeningPcss ||
            light.BlockerSamples != 8 || light.FilterSamples != 8 ||
            light.ShadowMapResolutionWidth == 0 || light.ShadowMapResolutionHeight == 0 ||
            width > 2048 || height > 2048)
            throw ShadowUnsupported($"light '{light.Name}' requires non-cascaded standalone depth, PCSS 8/8, no contact shadows, and effective dimensions at most 2048");
        if (light.ShadowCamera is not { DepthMode: XRCamera.EDepthMode.Normal, Parameters: XROrthographicCameraParameters })
            throw ShadowUnsupported($"light '{light.Name}' requires its authored normal-Z orthographic shadow camera");
    }

    private void PublishDirectionalShadow(WebGpuRenderProgram program, IRuntimeRenderWorld world)
    {
        DirectionalLightComponent? selected = null;
        int selectedIndex = 0;
        for (int i = 0; i < world.Lights.DynamicDirectionalLights.Count; i++)
        {
            DirectionalLightComponent light = world.Lights.DynamicDirectionalLights[i];
            if (!light.CastsShadows) continue;
            if (selected is not null) throw ShadowUnsupported("more than one directional shadow map was published");
            selected = light;
            selectedIndex = i;
        }
        XRTexture2D texture;
        if (selected is null)
        {
            texture = EnsureDefaultDirectionalShadow();
            program.SetVector4("DirectionalShadowControl", Vector4.Zero);
            program.SetMatrix("DirectionalShadowViewProjection", Matrix4x4.Identity);
            program.SetVector4("DirectionalShadowBiasProjection", Vector4.Zero);
            program.SetVector4("DirectionalShadowBiasParams", Vector4.Zero);
            program.SetVector4("DirectionalShadowFilterParams", Vector4.Zero);
            program.SetVector4("DirectionalShadowSourceParams", Vector4.Zero);
        }
        else
        {
            ValidateDirectionalShadowLight(selected);
            texture = selected.PrimaryShadowReceiverTexture as XRTexture2D
                ?? throw ShadowUnsupported($"light '{selected.Name}' has no exact 2D depth receiver texture");
            WebGpuTexture2D api = (WebGpuTexture2D)GetOrCreateAPIRenderObject(texture, generateNow: true)!;
            // A required map is never substituted with the disabled-binding default.
            // An unready nested shadow viewport causes the complete frame to defer.
            if (!api.WasProducedInFrame(_engineFrameSequence) && !CanPublishReusedShadow(selected, texture))
                MarkEngineDrawPending();
            program.SetMatrix("DirectionalShadowViewProjection", selected.ShadowCamera!.ViewProjectionMatrix);
            program.SetVector4("DirectionalShadowControl", new Vector4(1, selectedIndex, 0, 0));
            program.SetVector4("DirectionalShadowBiasProjection", selected.ShadowBiasProjectionParameters);
            program.SetVector4("DirectionalShadowBiasParams", selected.ShadowBiasParameters);
            program.SetVector4("DirectionalShadowFilterParams", new Vector4(selected.BlockerSearchRadius,
                selected.FilterRadius, selected.MinPenumbra, selected.MaxPenumbra));
            program.SetVector4("DirectionalShadowSourceParams", new Vector4(selected.EffectiveLightSourceRadius,
                selected.ShadowMinBias, selected.ShadowMaxBias, 0));
        }
        program.Data.Sampler("DirectionalShadowMap", texture, 0);
    }

    private XRTexture2D EnsureDefaultDirectionalShadow()
    {
        if (_defaultDirectionalShadow is null)
        {
            using ObjectCachePublicationScope ownership = XRObjectBase.BeginIndependentObjectCachePublication();
            using IDisposable suppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
            XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(1, 1, EPixelInternalFormat.DepthComponent24,
                EPixelFormat.DepthComponent, EPixelType.UnsignedInt, EFrameBufferAttachment.DepthAttachment);
            texture.Name = "Disabled directional shadow depth";
            texture.SizedInternalFormat = ESizedInternalFormat.DepthComponent24;
            texture.AutoGenerateMipmaps = false;
            texture.EnableComparison = true;
            texture.CompareFunc = ETextureCompareFunc.LessOrEqual;
            texture.MinFilter = ETexMinFilter.Nearest;
            texture.MagFilter = ETexMagFilter.Nearest;
            texture.UWrap = texture.VWrap = ETexWrapMode.ClampToEdge;
            texture.MaxAnisotropy = 1;
            texture.MinLOD = texture.MaxLOD = 0;
            SetField(ref _defaultDirectionalShadowOwnership, ownership.CompleteWithOwnership());
            SetField(ref _defaultDirectionalShadow, texture);
        }
        if (!_defaultDirectionalShadowInitialized && _defaultDirectionalShadowClearFrame != _engineFrameSequence)
        {
            if (_defaultDirectionalShadowClear == 0)
            {
                WebGpuTexture2D api = (WebGpuTexture2D)GetOrCreateAPIRenderObject(_defaultDirectionalShadow, generateNow: true)!;
                BrowserFrameBufferPlan plan = new([], new BrowserDepthStencilAttachmentPlan(api.GetRenderView(0, -1), depthClearValue: 1));
                SetField(ref _defaultDirectionalShadowClear, PrepareCommands(
                    "{\"label\":\"Disabled shadow depth initialization\",\"commands\":[{\"type\":\"clear\",\"pass\":" + plan.ToJson() + "}]}"));
            }
            RecordEngineCommands(_defaultDirectionalShadowClear, []);
            SetField(ref _defaultDirectionalShadowClearFrame, _engineFrameSequence, publishNotifications: false);
        }
        return _defaultDirectionalShadow!;
    }

    private void CommitDirectionalShadowDefaults()
    {
        CommitLocalShadowDefaults();
        if (_defaultDirectionalShadowClearFrame == _engineFrameSequence)
            SetField(ref _defaultDirectionalShadowInitialized, true, publishNotifications: false);
    }

    private void DestroyDirectionalShadowDefaults()
    {
        DestroyLocalShadowDefaults();
        _defaultDirectionalShadowOwnership?.Dispose();
        SetField(ref _defaultDirectionalShadowOwnership, null);
        SetField(ref _defaultDirectionalShadow, null);
        SetField(ref _defaultDirectionalShadowClear, 0);
        SetField(ref _defaultDirectionalShadowClearFrame, 0u);
        SetField(ref _defaultDirectionalShadowInitialized, false);
    }

    private static NotSupportedException ShadowUnsupported(string reason)
        => new($"WebGPU.Shadows.ProfileUnsupported: {reason}.");
}
