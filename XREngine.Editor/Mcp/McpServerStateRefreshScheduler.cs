namespace XREngine.Editor.Mcp
{
    /// <summary>
    /// Coalesces requests to apply the MCP server state from the editor preferences.
    /// </summary>
    /// <remarks>
    /// Requests that arrive while a refresh is pending join it, and none is lost: a refresh repeats
    /// until no request arrived during its last pass. A refresh does not apply the state while
    /// the effective preferences are being composed, because a composition can carry partial
    /// values (the global value of a setting before its override or session value). The end of a
    /// composition must request a new refresh.
    /// </remarks>
    internal sealed class McpServerStateRefreshScheduler(
        Func<bool> isComposing,
        Action applyServerState,
        Func<Task> settleDelay,
        Action<Exception> reportError)
    {
        private int _pending;
        private long _requests;

        /// <summary>True while a refresh is scheduled or running.</summary>
        public bool IsPending => Volatile.Read(ref _pending) != 0;

        /// <summary>Requests one refresh.</summary>
        public void Request()
        {
            Interlocked.Increment(ref _requests);
            if (Interlocked.Exchange(ref _pending, 1) != 0)
                return;

            _ = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            long handled = 0L;
            try
            {
                do
                {
                    handled = Volatile.Read(ref _requests);
                    await settleDelay().ConfigureAwait(false);
                    if (isComposing())
                        continue;
                    applyServerState();
                }
                while (Volatile.Read(ref _requests) != handled);
            }
            catch (Exception ex)
            {
                reportError(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _pending, 0);
            }

            // A request that arrived after the last check saw this refresh pending and returned.
            if (Volatile.Read(ref _requests) != handled)
                Request();
        }
    }
}
