namespace XREngine
{
    /// <summary>
    /// Signal for the job scheduler to requeue this job and resume on the next dispatch.
    /// </summary>
    internal sealed class WaitForNextDispatch
    {
        public static readonly WaitForNextDispatch Instance = new();
        private WaitForNextDispatch() { }
    }
}
