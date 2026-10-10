using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;

namespace XREngine.Components;

public partial class CameraComponent
{
    private RenderPipeline? _renderPipelineSource;
    private CameraPostProcessStateCollection? _authoredPostProcessStates = new();
    private EAntiAliasingMode? _authoredAntiAliasingModeOverride;
    private uint? _authoredMsaaSampleCountOverride;
    private bool? _authoredOutputHDROverride;

    /// <summary>Saved pipeline source, or null to use the application's default-source policy.</summary>
    [Category("Rendering")]
    public RenderPipeline? RenderPipelineSource
    {
        get
        {
            if (!_camera.IsValueCreated)
                return _renderPipelineSource;
            _camera.Value.TryGetAssignedRenderPipeline(out RenderPipeline? pipeline);
            return pipeline;
        }
        set
        {
            if (_camera.IsValueCreated)
                _camera.Value.SetRenderPipelineSource(value);
            else
                SetField(ref _renderPipelineSource, value);
        }
    }

    /// <summary>Saved per-pipeline and unassigned-default effect settings, available before camera realization.</summary>
    [Category("Rendering")]
    public CameraPostProcessStateCollection PostProcessStates
    {
        get => _camera.IsValueCreated ? _camera.Value.PostProcessStates : _authoredPostProcessStates ??= new();
        set
        {
            value ??= new();
            if (_camera.IsValueCreated)
                _camera.Value.PostProcessStates = value;
            else
                SetField(ref _authoredPostProcessStates, value);
        }
    }

    [Category("Rendering")]
    [DisplayName("Anti-Aliasing Override")]
    [Description("Optional per-camera anti-aliasing override. Null uses the global settings cascade.")]
    public EAntiAliasingMode? AntiAliasingModeOverride
    {
        get => _camera.IsValueCreated ? _camera.Value.AntiAliasingModeOverride : _authoredAntiAliasingModeOverride;
        set
        {
            if (_camera.IsValueCreated)
                _camera.Value.AntiAliasingModeOverride = value;
            else
                SetField(ref _authoredAntiAliasingModeOverride, value);
        }
    }

    [Category("Rendering")]
    public uint? MsaaSampleCountOverride
    {
        get => _camera.IsValueCreated ? _camera.Value.MsaaSampleCountOverride : _authoredMsaaSampleCountOverride;
        set
        {
            if (_camera.IsValueCreated)
                _camera.Value.MsaaSampleCountOverride = value;
            else
                SetField(ref _authoredMsaaSampleCountOverride, value);
        }
    }

    [Category("Rendering")]
    public bool? OutputHDROverride
    {
        get => _camera.IsValueCreated ? _camera.Value.OutputHDROverride : _authoredOutputHDROverride;
        set
        {
            if (_camera.IsValueCreated)
                _camera.Value.OutputHDROverride = value;
            else
                SetField(ref _authoredOutputHDROverride, value);
        }
    }

    private void ApplyAuthoredCameraState(XRCamera camera)
    {
        camera.SetRenderPipelineSource(_renderPipelineSource);
        camera.PostProcessStates = _authoredPostProcessStates ?? new();
        camera.AntiAliasingModeOverride = _authoredAntiAliasingModeOverride;
        camera.MsaaSampleCountOverride = _authoredMsaaSampleCountOverride;
        camera.OutputHDROverride = _authoredOutputHDROverride;
    }

    private void ReleaseCachedAuthoredCameraState()
    {
        _renderPipelineSource = null;
        _authoredPostProcessStates = null;
        _authoredAntiAliasingModeOverride = null;
        _authoredMsaaSampleCountOverride = null;
        _authoredOutputHDROverride = null;
    }

    private void NotifyAuthoredCameraStateChanged(IXRPropertyChangedEventArgs args)
    {
        string? name = args.PropertyName switch
        {
            nameof(XRCamera.RenderPipeline) => nameof(RenderPipelineSource),
            nameof(XRCamera.PostProcessStates) => nameof(PostProcessStates),
            nameof(XRCamera.AntiAliasingModeOverride) => nameof(AntiAliasingModeOverride),
            nameof(XRCamera.MsaaSampleCountOverride) => nameof(MsaaSampleCountOverride),
            nameof(XRCamera.OutputHDROverride) => nameof(OutputHDROverride),
            _ => null
        };
        if (name is not null)
            OnPropertyChanged(name, args.PreviousValue, args.NewValue);
    }
}
