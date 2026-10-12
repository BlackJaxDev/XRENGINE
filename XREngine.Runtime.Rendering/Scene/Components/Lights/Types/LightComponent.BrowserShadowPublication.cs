using System.Numerics;
using XREngine.Components.Lights;
using XREngine.Rendering;

namespace XREngine.Components.Capture.Lights.Types;

public abstract partial class LightComponent
{
    /// <summary>Reads a matching producer candidate without changing main-output publication metadata.</summary>
    internal bool TryGetBrowserShadowSnapshot(ulong outputGeneration, IBrowserShadowReuseCapability renderer,
        out AdvancedShadowRecord record, out XRTexture? texture)
    {
        record = _browserRenderedShadowRecord;
        texture = _browserCachedShadowReceiver;
        return _browserRenderedShadowValid && CastsShadows && texture is not null &&
            ReferenceEquals(_browserCachedShadowRenderer, renderer) &&
            ReferenceEquals(GetBrowserShadowReceiver(), texture) &&
            _browserCachedShadowOutputGeneration == outputGeneration &&
            _browserCachedShadowProductionTicket != 0 &&
            _browserCachedShadowProductionTicket == renderer.GetShadowProductionTicket(texture) &&
            TryCreateBrowserShadowRecord(_browserCachedShadowFrame, out AdvancedShadowRecord current, out XRTexture? receiver) &&
            ReferenceEquals(receiver, texture) && BrowserShadowPayloadsMatch(in record, in current) &&
            TryCaptureBrowserShadowProjections(out BrowserShadowProjectionSnapshot projections) &&
            BrowserShadowProjectionsMatch(in projections, in _browserRenderedShadowProjections) &&
            this is not PointLightComponent { LastRenderedShadowFaceMask: not 63 };
    }
    private AdvancedShadowRecord _browserPendingShadowRecord;
    private AdvancedShadowRecord _browserRenderedShadowRecord;
    private XRTexture? _browserPendingShadowReceiver;
    private XRMaterialFrameBuffer? _browserPendingShadowMap;
    private ulong _browserPendingShadowProductionTicket;
    private BrowserShadowProjectionSnapshot _browserPendingShadowProjections;
    private BrowserShadowProjectionSnapshot _browserRenderedShadowProjections;
    private BrowserShadowProjectionSnapshot _browserPublishedShadowProjections;
    private XRTexture? _browserPublishedShadowReceiver;
    private ulong _browserPublishedShadowFrame;
    private bool _browserPendingShadowValid;
    private bool _browserRenderedShadowValid;

    /// <summary>
    /// Freezes the standalone producer inputs before command recording. This is
    /// a candidate, never evidence that a previous texture contains this projection.
    /// </summary>
    internal bool TryCaptureBrowserStandaloneShadow(ulong frameId,
        out AdvancedShadowRecord record, out XRTexture? texture)
    {
        SetField(ref _browserPublishedShadowReceiver, null, publishNotifications: false);
        if (!TryCreateBrowserShadowRecord(frameId, out record, out texture) ||
            !TryCaptureBrowserShadowProjections(out BrowserShadowProjectionSnapshot projections))
            return false;
        SetField(ref _browserPublishedShadowProjections, projections, publishNotifications: false);
        SetField(ref _browserPublishedShadowReceiver, texture, publishNotifications: false);
        SetField(ref _browserPublishedShadowFrame, frameId, publishNotifications: false);
        return true;
    }

    private bool TryCreateBrowserShadowRecord(ulong frameId,
        out AdvancedShadowRecord record, out XRTexture? texture)
    {
        record = default;
        texture = GetBrowserShadowReceiver();
        if (!CastsShadows || ShadowMap is null || texture is null)
            return false;

        XRCamera? camera;
        EAdvancedShadowType type;
        switch (this)
        {
            case DirectionalLightComponent directional when !directional.UseShadowAtlas && !directional.EnableCascadedShadows:
                camera = directional.ShadowCamera;
                type = EAdvancedShadowType.DirectionalCascade;
                break;
            case SpotLightComponent spot when !spot.UseShadowAtlas:
                camera = spot.ShadowCamera;
                type = EAdvancedShadowType.Spot;
                break;
            case PointLightComponent point when !point.UseShadowAtlas && point.TryGetShadowFaceCamera(0, out XRCamera face):
                camera = face;
                type = EAdvancedShadowType.PointCube;
                break;
            default:
                return false;
        }
        if (camera is null)
            return false;

        Vector4 bias = ShadowBiasParameters;
        Vector4 projectionBias = type == EAdvancedShadowType.DirectionalCascade
            ? ShadowBiasProjectionParameters : Vector4.Zero;
        Matrix4x4 worldToShadow = type == EAdvancedShadowType.PointCube
            ? Matrix4x4.Identity : camera.GetViewProjectionMatrix(RuntimeGraphicsApiKind.WebGPU);
        record = new AdvancedShadowRecord
        {
            Type = type,
            Flags = EAdvancedShadowRecordFlags.Resident | EAdvancedShadowRecordFlags.DepthZeroToOne |
                EAdvancedShadowRecordFlags.FramebufferTextureYDown | EAdvancedShadowRecordFlags.BrowserStandalonePcss |
                EAdvancedShadowRecordFlags.BrowserStandaloneCandidate |
                (camera.DepthMode == XRCamera.EDepthMode.Reversed ? EAdvancedShadowRecordFlags.ReversedDepth : EAdvancedShadowRecordFlags.None),
            WorldToShadow = worldToShadow,
            PreviousWorldToShadow = worldToShadow,
            UvScaleBias = this is SpotLightComponent standaloneSpot
                ? new Vector4(standaloneSpot.Transform.RenderForward, standaloneSpot.OuterCutoff)
                : new Vector4(1.0f, 1.0f, 0.0f, 0.0f),
            DepthBiasAndFilter = new Vector4(bias.X, bias.Y, bias.Z, EffectiveLightSourceRadius),
            MomentParameters = new Vector4(BlockerSearchRadius, FilterRadius, MinPenumbra, MaxPenumbra),
            DepthRangeAndCascade = new Vector4(camera.NearZ, camera.FarZ, projectionBias.X, projectionBias.Y),
            RenderedLightPositionAndFar = new Vector4(Transform.RenderTranslation, camera.FarZ),
            Encoding = (uint)ShadowMapEncoding,
            CascadeCount = 1u,
            LastRenderedFrameLo = (uint)frameId,
            LastRenderedFrameHi = (uint)(frameId >> 32),
        };
        return true;
    }

    /// <summary>Captures the exact inputs and prior receipt before the ordered producer commands.</summary>
    internal void BeginBrowserShadowRender(ulong frameId, IBrowserShadowReuseCapability reuse)
    {
        SetField(ref _browserPendingShadowValid, false, publishNotifications: false);
        if (!TryCreateBrowserShadowRecord(frameId, out AdvancedShadowRecord record, out XRTexture? texture) ||
            !TryCaptureBrowserShadowProjections(out BrowserShadowProjectionSnapshot projections))
            return;
        SetField(ref _browserPendingShadowRecord, record, publishNotifications: false);
        SetField(ref _browserPendingShadowReceiver, texture, publishNotifications: false);
        SetField(ref _browserPendingShadowMap, ShadowMap, publishNotifications: false);
        SetField(ref _browserPendingShadowProductionTicket, reuse.GetShadowProductionTicket(texture!), publishNotifications: false);
        SetField(ref _browserPendingShadowProjections, projections, publishNotifications: false);
        SetField(ref _browserPendingShadowValid, true, publishNotifications: false);
    }

    /// <summary>A declined nested viewport must never issue a standalone producer receipt.</summary>
    internal void RejectBrowserShadowRender()
        => SetField(ref _browserPendingShadowValid, false, publishNotifications: false);

    private bool TryCompleteBrowserShadowRender(ulong frameId, IBrowserShadowReuseCapability reuse,
        out AdvancedShadowRecord record, out XRTexture? receiver)
    {
        bool pending = _browserPendingShadowValid;
        SetField(ref _browserPendingShadowValid, false, publishNotifications: false);
        if (!pending || !TryCreateBrowserShadowRecord(frameId, out record, out receiver))
        {
            record = default;
            receiver = null;
            return false;
        }
        if (!ReferenceEquals(_browserPendingShadowMap, ShadowMap) ||
            !ReferenceEquals(_browserPendingShadowReceiver, receiver) ||
            !TryCaptureBrowserShadowProjections(out BrowserShadowProjectionSnapshot projections) ||
            !BrowserShadowProjectionsMatch(in _browserPendingShadowProjections, in projections) ||
            !BrowserShadowPayloadsMatch(in _browserPendingShadowRecord, in record) ||
            !reuse.WasShadowProducedInCurrentFrame(receiver!) ||
            reuse.GetShadowProductionTicket(receiver!) == _browserPendingShadowProductionTicket ||
            this is PointLightComponent { LastRenderedShadowFaceMask: not 63 })
            return false;

        record.Flags &= ~EAdvancedShadowRecordFlags.BrowserStandaloneCandidate;
        SetField(ref _browserRenderedShadowProjections, projections, publishNotifications: false);
        return true;
    }

    /// <summary>
    /// Validates an immutable canonical candidate against the exact recorded
    /// producer receipt. The backend separately proves current-frame production
    /// or authorizes committed cadence reuse before admitting its native consumer.
    /// </summary>
    internal bool MatchesBrowserShadowPublication(in AdvancedShadowRecord record, XRTexture texture,
        ulong outputGeneration, IBrowserShadowReuseCapability reuse)
        => _browserRenderedShadowValid && CastsShadows &&
           _browserPublishedShadowFrame == ((ulong)record.LastRenderedFrameHi << 32 | record.LastRenderedFrameLo) &&
           ReferenceEquals(_browserPublishedShadowReceiver, texture) &&
           ReferenceEquals(_browserCachedShadowMap, ShadowMap) &&
           ReferenceEquals(_browserCachedShadowReceiver, texture) &&
           ReferenceEquals(GetBrowserShadowReceiver(), texture) &&
           ReferenceEquals(_browserCachedShadowRenderer, reuse) &&
           _browserCachedShadowOutputGeneration == outputGeneration &&
           _browserCachedShadowProductionTicket != 0 &&
           _browserCachedShadowProductionTicket == reuse.GetShadowProductionTicket(texture) &&
           BrowserShadowProjectionsMatch(in _browserPublishedShadowProjections, in _browserRenderedShadowProjections) &&
           BrowserShadowPayloadsMatch(in record, in _browserRenderedShadowRecord);

    private bool TryCaptureBrowserShadowProjections(out BrowserShadowProjectionSnapshot projections)
    {
        projections = default;
        if (this is not PointLightComponent point)
            return true;
        for (int face = 0; face < PointLightComponent.ShadowFaceCount; face++)
        {
            if (!point.TryGetShadowFaceCamera(face, out XRCamera camera))
                return false;
            projections[face] = camera.GetViewProjectionMatrix(RuntimeGraphicsApiKind.WebGPU);
        }
        return true;
    }

    private static bool BrowserShadowProjectionsMatch(in BrowserShadowProjectionSnapshot left,
        in BrowserShadowProjectionSnapshot right)
    {
        for (int face = 0; face < PointLightComponent.ShadowFaceCount; face++)
            if (left[face] != right[face])
                return false;
        return true;
    }

    private bool BrowserShadowReuseMatchesCurrentProjection(ulong frameId)
        => TryCreateBrowserShadowRecord(frameId, out AdvancedShadowRecord record, out XRTexture? receiver) &&
           ReferenceEquals(_browserCachedShadowReceiver, receiver) &&
           BrowserShadowPayloadsMatch(in record, in _browserRenderedShadowRecord) &&
           TryCaptureBrowserShadowProjections(out BrowserShadowProjectionSnapshot projections) &&
           BrowserShadowProjectionsMatch(in projections, in _browserRenderedShadowProjections);

    internal static bool BrowserShadowPayloadsMatch(in AdvancedShadowRecord left, in AdvancedShadowRecord right)
        => left.Type == right.Type &&
           (left.Flags & ~EAdvancedShadowRecordFlags.BrowserStandaloneCandidate) ==
               (right.Flags & ~EAdvancedShadowRecordFlags.BrowserStandaloneCandidate) &&
           left.WorldToShadow == right.WorldToShadow && left.PreviousWorldToShadow == right.PreviousWorldToShadow &&
           left.UvScaleBias == right.UvScaleBias && left.DepthBiasAndFilter == right.DepthBiasAndFilter &&
           left.MomentParameters == right.MomentParameters && left.DepthRangeAndCascade == right.DepthRangeAndCascade &&
           left.RenderedLightPositionAndFar == right.RenderedLightPositionAndFar &&
           left.TextureLayer == right.TextureLayer && left.Encoding == right.Encoding && left.CascadeCount == right.CascadeCount &&
           left.ViewMaskLo == right.ViewMaskLo && left.ViewMaskHi == right.ViewMaskHi;

    private void ResetBrowserShadowPublication()
    {
        SetField(ref _browserPendingShadowValid, false, publishNotifications: false);
        SetField(ref _browserRenderedShadowValid, false, publishNotifications: false);
        SetField(ref _browserPendingShadowReceiver, null, publishNotifications: false);
        SetField(ref _browserPendingShadowMap, null, publishNotifications: false);
        SetField(ref _browserPublishedShadowReceiver, null, publishNotifications: false);
        SetField(ref _browserPublishedShadowFrame, 0UL, publishNotifications: false);
    }
}
