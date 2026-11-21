namespace XREngine.UnityScriptHost.Execution;

/// <summary>Clears the worker API context without clearing its recorded failures.</summary>
internal sealed class UnityCompatibilitySession(UnityBuildExecutionState state) : IDisposable
{
    internal UnityBuildExecutionState State { get; } = state;

    public void Dispose()
    {
        UnityCompatibilityRuntime.EndSession(this);
    }
}
