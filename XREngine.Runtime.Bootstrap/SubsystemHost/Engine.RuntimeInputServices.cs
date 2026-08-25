using XREngine.Input;

namespace XREngine;

internal sealed class EngineRuntimeInputServices : IRuntimeInputServices
{
    public float UpdateDeltaSeconds => Engine.Delta;
    public bool IsUIInputCaptured => Engine.Input.IsUIInputCaptured;
}
