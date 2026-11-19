namespace XREngine.Rendering.OutputDomains;

/// <summary>
/// Holds the output domain of the render work on the current thread. A backend frame entry
/// point (the desktop frame transaction, the XR eye frame, a render worker that records for one
/// of them) enters a scope, and code below it reads <see cref="Current"/> to choose the state
/// that this domain owns and to count its work.
/// </summary>
public static class RenderOutputDomainContext
{
    [ThreadStatic]
    private static ERenderOutputDomain t_current;

    /// <summary>Gets the domain of the innermost active scope on this thread, or <see cref="ERenderOutputDomain.None"/>.</summary>
    public static ERenderOutputDomain Current => t_current;

    /// <summary>
    /// Makes <paramref name="domain"/> the current domain of this thread until the returned
    /// scope is disposed. Scopes nest: disposing restores the previous domain. The scope is a
    /// struct, so entering one allocates nothing.
    /// </summary>
    public static Scope Enter(ERenderOutputDomain domain)
    {
        ERenderOutputDomain previous = t_current;
        t_current = domain;
        return new Scope(previous);
    }

    /// <summary>Restores the previous domain of the thread when disposed.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly ERenderOutputDomain _previous;

        internal Scope(ERenderOutputDomain previous)
            => _previous = previous;

        /// <summary>Restores the domain that was current before the scope was entered.</summary>
        public void Dispose()
            => t_current = _previous;
    }
}
