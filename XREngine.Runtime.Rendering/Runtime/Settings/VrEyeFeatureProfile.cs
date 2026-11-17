using MemoryPack;
using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Rendering.PostProcessing;

namespace XREngine;

/// <summary>
/// Selects the optional rendering features of VR eye frames. One profile applies to every eye
/// frame: single-pass stereo and per-eye (sequential views and parallel command buffer
/// recording), the Default, Advanced and RVC pipelines, and OpenXR and OpenVR. The two eyes
/// always use the same profile, and the desktop and other cameras never read it.
/// </summary>
/// <remarks>
/// <para>
/// A feature runs in an eye frame only when the profile allows it and the eye camera's own
/// settings enable it (for example, an ambient occlusion stage with <c>Enabled</c> on). The
/// profile cannot turn on a feature that the active pipeline does not support for that view,
/// such as a stereo resource profile without the feature.
/// </para>
/// <para>
/// The defaults keep depth of field and motion blur off, because both effects reduce comfort in
/// a headset. Forward+ light culling stays on: without it, forward and transparent surfaces in
/// an eye frame get no local lights.
/// </para>
/// </remarks>
[Serializable]
[MemoryPackable]
public partial class VrEyeFeatureProfile : XRBase
{
    private bool _ambientOcclusion = true;
    private bool _fogAndAerialPerspective = true;
    private bool _orderIndependentTransparency = true;
    private bool _depthOfField;
    private bool _motionBlur;
    private bool _deferredMsaa = true;
    private bool _forwardPlusLightCulling = true;

    /// <summary>Allows the camera ambient occlusion passes (for example GTAO) in eye frames.</summary>
    [Category("VR Eye Features")]
    [DisplayName("Ambient Occlusion")]
    [Description("Allows camera ambient occlusion (for example GTAO) in VR eye frames. The eye camera's ambient occlusion settings still select the mode.")]
    public bool AmbientOcclusion
    {
        get => _ambientOcclusion;
        set => SetField(ref _ambientOcclusion, value);
    }

    /// <summary>Allows volumetric fog and atmospheric aerial perspective in eye frames.</summary>
    [Category("VR Eye Features")]
    [DisplayName("Fog and Aerial Perspective")]
    [Description("Allows volumetric fog and atmospheric scattering aerial perspective in VR eye frames.")]
    public bool FogAndAerialPerspective
    {
        get => _fogAndAerialPerspective;
        set => SetField(ref _fogAndAerialPerspective, value);
    }

    /// <summary>Allows weighted blended OIT and the exact transparency techniques in eye frames.</summary>
    [Category("VR Eye Features")]
    [DisplayName("Order-Independent Transparency")]
    [Description("Allows weighted blended order-independent transparency and the exact transparency techniques in VR eye frames.")]
    public bool OrderIndependentTransparency
    {
        get => _orderIndependentTransparency;
        set => SetField(ref _orderIndependentTransparency, value);
    }

    /// <summary>Allows depth of field in eye frames. Off by default for comfort.</summary>
    [Category("VR Eye Features")]
    [DisplayName("Depth of Field")]
    [Description("Allows depth of field in VR eye frames. Off by default, because depth of field in a headset reduces comfort.")]
    public bool DepthOfField
    {
        get => _depthOfField;
        set => SetField(ref _depthOfField, value);
    }

    /// <summary>Allows motion blur in eye frames. Off by default for comfort.</summary>
    [Category("VR Eye Features")]
    [DisplayName("Motion Blur")]
    [Description("Allows motion blur in VR eye frames. Off by default, because motion blur in a headset reduces comfort.")]
    public bool MotionBlur
    {
        get => _motionBlur;
        set => SetField(ref _motionBlur, value);
    }

    /// <summary>Allows the deferred MSAA path in eye frames when the eye camera selects MSAA.</summary>
    [Category("VR Eye Features")]
    [DisplayName("Deferred MSAA")]
    [Description("Allows the deferred MSAA path in VR eye frames when the eye camera selects MSAA anti-aliasing.")]
    public bool DeferredMsaa
    {
        get => _deferredMsaa;
        set => SetField(ref _deferredMsaa, value);
    }

    /// <summary>
    /// Allows Forward+ local light culling in eye frames. Without it, forward and transparent
    /// surfaces in an eye frame get no local lights.
    /// </summary>
    [Category("VR Eye Features")]
    [DisplayName("Forward+ Light Culling")]
    [Description("Allows Forward+ local light culling in VR eye frames. Without it, forward and transparent surfaces in the eyes get no local lights.")]
    public bool ForwardPlusLightCulling
    {
        get => _forwardPlusLightCulling;
        set => SetField(ref _forwardPlusLightCulling, value);
    }

    /// <summary>
    /// False when this profile turns off the feature that the post-process stage
    /// <paramref name="stageKey"/> (a <see cref="CommonPostProcessStages.StageKeys"/> value) renders
    /// in eye frames: ambient occlusion, atmospheric scattering, volumetric fog, depth of field
    /// or motion blur. Other stages are not part of the profile and return true. Readouts use it
    /// to show the effective state of an eye camera's stage, whose own <c>Enabled</c> value does
    /// not change.
    /// </summary>
    public bool AllowsPostProcessStage(string? stageKey)
        => stageKey switch
        {
            CommonPostProcessStages.StageKeys.AmbientOcclusion => AmbientOcclusion,
            CommonPostProcessStages.StageKeys.AtmosphericScattering or
            CommonPostProcessStages.StageKeys.VolumetricFog => FogAndAerialPerspective,
            CommonPostProcessStages.StageKeys.DepthOfField => DepthOfField,
            CommonPostProcessStages.StageKeys.MotionBlur => MotionBlur,
            _ => true,
        };
}
