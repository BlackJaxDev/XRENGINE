using XREngine.Publishing;

namespace XREngine.Tools.BrowserContentCooker;

/// <summary>Command-line entry point for the shared browser content packager.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: BrowserContentCooker <recipe.json> <output-directory>");
            return 2;
        }
        try
        {
            BrowserContentPackageBuilder.Build(args[0], args[1]);
            Console.WriteLine("Published browser content manifest and immutable payloads.");
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidDataException or ArgumentException or OverflowException or InvalidOperationException
            or KeyNotFoundException or FormatException)
        {
            Console.Error.WriteLine($"Content cook failed: {error.Message}");
            return 1;
        }
    }
}
