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
            => Factory.StartNew(func).Unwrap().GetAwaiter().GetResult();
        /// <summary>
        /// Runs an async method synchronously.
        /// </summary>
        public static void RunSync(this Func<Task> func) 
            => Factory.StartNew(func).Unwrap().GetAwaiter().GetResult();
        
        public static async Task<TBase> Generalized<TBase, TDerived>(this Task<TDerived> task) where TDerived : TBase => await task;
    }
}
