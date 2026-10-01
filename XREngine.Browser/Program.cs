using XREngine.Scene.Physics.Jolt;
using XREngine.Audio.WebAudio;

namespace XREngine.Browser;

internal static class Program
{
    private static void Main()
    {
        BrowserStaticRegistrations.Initialize();
        BrowserRendererComposition.Initialize();
        BrowserEngineExports.InstallPhysicsBackend(new JoltPhysicsBackendModule());
        WebAudioTransport.Register();
        Console.WriteLine("XRENGINE browser runtime loaded.");
    }
}
