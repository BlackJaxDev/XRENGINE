namespace XREngine.Runtime.Bootstrap;

public static class RuntimeBootstrapState
{
    public static UnitTestingWorldSettings Settings { get; set; } = new();
    public static bool DeferNonEssentialStartupWorkUntilStartupCompletes { get; set; }
}