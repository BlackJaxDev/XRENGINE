using Silk.NET.OpenXR;
using XREngine;
using XREngine.Rendering;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    #region Public API + window binding

    /// <summary>
    /// Gets the OpenXR API instance.
    /// </summary>
    public XR Api { get; private set; }

    /// <summary>
    /// Gets or sets the window associated with this XR session.
    /// Setting a new window triggers initialization or cleanup as appropriate.
    /// </summary>
    public XRWindow? Window
    {
        get => _window;
        set => SetField(ref _window, value);
    }

    /// <summary>
    /// Called before a property changes to perform any necessary cleanup.
    /// </summary>
    protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
    {
        bool change = base.OnPropertyChanging(propName, field, @new);
        if (change)
        {
            switch (propName)
            {
                case nameof(Window):
                    if (field is not null)
                        CleanUp();
                    break;
            }
        }
        return change;
    }

    /// <summary>
    /// Called after a property changes to perform any necessary initialization.
    /// </summary>
    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(Window):
                if (field is not null)
                    Initialize();
                break;
        }
    }

    /// <summary>
    /// Initializes the OpenXR session and associated resources.
    /// </summary>
    protected void Initialize()
    {
        EnableRuntimeMonitoring();
    }

    private void HookEngineTimerEvents()
    {
        // Hooking is owned by Engine.VRState to ensure OpenVR/OpenXR share the same engine callback entrypoints.
    }

    private void UnhookEngineTimerEvents()
    {
        // Hooking is owned by Engine.VRState to ensure OpenVR/OpenXR share the same engine callback entrypoints.
    }

    private XRViewport? TryGetSourceViewport()
    {
        if (Window is null)
            return null;

        foreach (var vp in Window.Viewports)
        {
            if (vp is null)
                continue;
            if (vp.World is null)
                continue;
            if (vp.ActiveCamera is null)
                continue;
            return vp;
        }

        return null;
    }

    #endregion
}
