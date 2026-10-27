using Engine.Diagnostics;

namespace XREngine.UnitTests.HotPathAnalyzers;

/// <summary>Requires the compiler to load the generator and analyzer during the test build.</summary>
internal static class HotPathCompilerHostSmoke
{
    [HotPath]
    public static int Increment(int value) => value + 1;
}
