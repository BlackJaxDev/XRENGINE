using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Browser;

public static partial class BrowserEngineExports
{
    /// <summary>Replaces the GPU owner without stopping or reloading the authored engine world.</summary>
    [JSExport]
    public static int BeginCanvasRecovery(int session, bool deviceLost)
        => (_session ?? throw new InvalidOperationException("WebGPU.EngineCanvas.Required: no active engine world."))
            .BeginRendererRecovery(session, deviceLost);

    /// <summary>Resumes gameplay only after the replacement's first valid current-output submission.</summary>
    [JSExport]
    public static void CompleteCanvasRecovery(int session)
        => (_session ?? throw new InvalidOperationException("WebGPU.EngineCanvas.Required: no active engine world."))
            .CompleteRendererRecovery(session);

    /// <summary>Retains gameplay state while reporting that explicit restart is required.</summary>
    [JSExport]
    public static void SuspendCanvasRecovery(int session, string failure)
        => (_session ?? throw new InvalidOperationException("WebGPU.EngineCanvas.Required: no active engine world."))
            .SuspendRendererRecovery(session, failure);
}
