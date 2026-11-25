namespace XREngine.Rendering.UI;

/// <summary>Exposes a draw-local cache candidate only while its exact mesh is submitted.</summary>
internal readonly struct UIStaticDrawScope : IDisposable
{
    [ThreadStatic] private static UIStaticDrawCandidate t_current;
    private readonly UIStaticDrawCandidate _previous;

    internal UIStaticDrawScope(in UIStaticDrawCandidate candidate)
    {
        _previous = t_current;
        t_current = candidate;
    }

    internal static bool TryCapture(AbstractRenderer backend, XRMeshRenderer renderer,
        ulong receiptId, uint instances, out UIStaticDrawCandidate candidate)
    {
        candidate = t_current;
        if (candidate.Matches(backend, renderer, receiptId, instances))
            return true;
        candidate = default;
        return false;
    }

    public void Dispose() => t_current = _previous;
}
