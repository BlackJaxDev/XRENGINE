namespace XREngine.Extensions
{
    public static partial class TaskExtensions
    {
        private static readonly TaskFactory Factory = new(
            CancellationToken.None,
            TaskCreationOptions.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        /// <summary>
        /// Runs an async method synchronously.
        /// </summary>
        public static TResult RunSync<TResult>(this Func<Task<TResult>> func)
        {
            ArgumentNullException.ThrowIfNull(func);
            if (OperatingSystem.IsBrowser())
                throw new NotSupportedException("Runtime.AsyncOperationRequired: browser callbacks cannot synchronously wait for asynchronous work.");
            return Factory.StartNew(func).Unwrap().GetAwaiter().GetResult();
        }
        /// <summary>
        /// Runs an async method synchronously.
        /// </summary>
        public static void RunSync(this Func<Task> func)
        {
            ArgumentNullException.ThrowIfNull(func);
            if (OperatingSystem.IsBrowser())
                throw new NotSupportedException("Runtime.AsyncOperationRequired: browser callbacks cannot synchronously wait for asynchronous work.");
            Factory.StartNew(func).Unwrap().GetAwaiter().GetResult();
        }
        
        public static async Task<TBase> Generalized<TBase, TDerived>(this Task<TDerived> task) where TDerived : TBase => await task;
    }
}
