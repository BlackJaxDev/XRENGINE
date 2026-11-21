namespace XREngine.Rendering.Compute;

internal sealed partial class SkinningPrepassDispatcher
{
    /// <summary>
    /// Reads the last rejected output overwrite with the input state captured at rejection.
    /// The diagnostic does not read GPU buffer contents.
    /// </summary>
    public static OutputReuseFailureSnapshot? GetLastOutputReuseFailure()
    {
        SkinningPrepassDispatcher dispatcher = Instance;
        lock (dispatcher._syncRoot)
            return dispatcher._lastOutputReuseFailure;
    }
}
