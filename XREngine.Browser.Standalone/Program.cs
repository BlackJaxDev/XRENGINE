using XREngine.Browser;

namespace XREngine.Browser.Standalone;

internal static class Program
{
    private static void Main()
    {
        BrowserRuntime.Initialize();
        Console.WriteLine("XRENGINE browser runtime loaded.");
    }
}
