namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Runs a cold Vulkan pipeline compile without occupying a thread-pool thread
/// while it waits for the renderer's compile-concurrency gate.
/// </summary>
internal static class VulkanPipelineCompileTask
{
    public static async Task<T> RunAsync<T>(
        SemaphoreSlim compileGate,
        Func<T> compile)
    {
        await compileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await RunOnDedicatedThread(compile).ConfigureAwait(false);
        }
        finally
        {
            compileGate.Release();
        }
    }

    private static Task<T> RunOnDedicatedThread<T>(Func<T> compile)
    {
        TaskCompletionSource<T> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread worker = new(() =>
        {
            try
            {
                completion.TrySetResult(compile());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "XRE Vulkan Pipeline Compile",
            Priority = ThreadPriority.BelowNormal,
        };

        worker.Start();
        return completion.Task;
    }
}
