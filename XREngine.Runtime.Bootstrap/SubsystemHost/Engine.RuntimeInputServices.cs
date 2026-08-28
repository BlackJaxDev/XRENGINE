using XREngine.Input;
using XREngine.Runtime.InputIntegration;

namespace XREngine;

internal sealed class EngineRuntimeInputServices : IRuntimeInputServices
{
    public float UpdateDeltaSeconds => Engine.Delta;
    public bool IsUIInputCaptured => RuntimeInputCaptureServices.Current.IsUIInputCaptured;
}
