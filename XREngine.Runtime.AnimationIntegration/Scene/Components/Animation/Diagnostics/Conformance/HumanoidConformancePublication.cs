namespace XREngine.Components.Animation;

/// <summary>Releases the raw source reservation before the host installation reservation.</summary>
internal sealed class HumanoidConformancePublication(IDisposable readPublication, IDisposable hostPublication) : IDisposable
{
    private IDisposable? _readPublication = readPublication;

    public void Dispose()
    {
        IDisposable? current = Interlocked.Exchange(ref _readPublication, null);
        if (current is null)
            return;
        try { current.Dispose(); }
        finally { hostPublication.Dispose(); }
    }
}
