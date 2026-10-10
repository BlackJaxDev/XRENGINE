using System.Runtime.InteropServices.JavaScript;
using XREngine.Audio.WebAudio;

namespace XREngine.Browser;

public static partial class BrowserEngineExports
{
    [JSExport]
    public static bool IsAudioRequired() => _session?.AudioRequired ?? false;

    [JSExport]
    public static string GetAudioFailure() => WebAudioTransport.ActivationFailure;

    /// <summary>Publishes page visibility separately from the canvas surface blocker.</summary>
    [JSExport]
    public static void SetAudioPageActive(bool active) => WebAudioTransport.SetPageActive(active);
}
