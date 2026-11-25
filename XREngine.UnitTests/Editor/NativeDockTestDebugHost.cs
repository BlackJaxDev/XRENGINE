namespace XREngine.UnitTests.Editor;

/// <summary>Keeps diagnostic callbacks synchronous without opening log files.</summary>
internal sealed class NativeDockTestDebugHost : IRuntimeDebugHostServices
{
    public bool LogOutputToFile => false;
    public EOutputVerbosity OutputVerbosity => EOutputVerbosity.Verbose;
    public double DebugOutputRecencySeconds => 0;
    public bool IsRenderThread => false;
}
