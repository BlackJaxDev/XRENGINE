namespace XREngine.Editor;

internal static class NativeDockPanel
{
    public const string Hierarchy = "hierarchy";
    public const string Scene = "scene";
    public const string Inspector = "inspector";
    public const string Assets = "assets";
    public const string Console = "console";
    public const string Profiler = "profiler";
    public const string Animation = "animation";

    public static readonly string[] All =
        [Hierarchy, Scene, Inspector, Assets, Console, Profiler, Animation];
    public static readonly string[] LegacyTools = [Assets, Console, Profiler, Animation];
}
