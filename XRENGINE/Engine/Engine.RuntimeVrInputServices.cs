using OpenVRAction = OpenVR.NET.Input.Action;
using XREngine.Input;

namespace XREngine;

internal sealed class EngineRuntimeVrInputServices : IRuntimeVrInputServices
{
    public event System.Action<Dictionary<string, Dictionary<string, OpenVRAction>>>? ActionsChanged
    {
        add => Engine.VRState.ActionsChanged += value;
        remove => Engine.VRState.ActionsChanged -= value;
    }

    public Dictionary<string, Dictionary<string, OpenVRAction>> Actions
        => Engine.VRState.Actions;
}