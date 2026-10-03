namespace XREngine.Browser;

internal static class Program
{
    private static void Main()
    {
        BrowserStaticRegistrations.Initialize();
        BrowserRendererComposition.Initialize();
        Console.WriteLine("XRENGINE portable scene host loaded.");
    }
}
