namespace XREngine.RenderBench;

using XREngine.Runtime.Bootstrap;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        RenderBenchOptions options;
        try
        {
            options = RenderBenchOptions.Parse(args);
        }
        catch (RenderBenchHelpRequestedException)
        {
            Console.WriteLine(RenderBenchOptions.Usage);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(RenderBenchOptions.Usage);
            return 2;
        }

        Directory.CreateDirectory(options.OutputDirectory);
        RuntimeRenderingBootstrap.InstallEngineHostServices();
        RenderBenchProcessState state = new(options);
        using EventWaitHandle? shutdownEvent = string.IsNullOrWhiteSpace(options.ShutdownEventName)
            ? null
            : new EventWaitHandle(false, EventResetMode.ManualReset, options.ShutdownEventName);
        await using RenderBenchMcpHost? mcpHost = options.McpPolicy == RenderBenchMcpPolicy.Disabled
            ? null
            : new RenderBenchMcpHost(options, state);

        try
        {
            if (mcpHost is not null)
            {
                state.SetPhase(options.WaitForMcpStart ? RenderBenchPhase.Idle : RenderBenchPhase.Starting);
                mcpHost.Start();
                Console.WriteLine($"RenderBench MCP ready: http://localhost:{options.McpPort}/mcp/ pid={Environment.ProcessId}");
            }

            if (options.WaitForMcpStart)
            {
                Task requested = await Task.WhenAny(state.StartRequested, state.ShutdownRequested).ConfigureAwait(false);
                if (requested == state.ShutdownRequested)
                    return 0;
            }

            // The measured interval is network-silent by construction.
            if (mcpHost is not null)
                await mcpHost.StopAsync().ConfigureAwait(false);

            bool ShutdownRequested()
                => state.IsShutdownRequested || shutdownEvent?.WaitOne(0) == true;

            string resultPath = new RenderBenchRunner(
                options,
                state,
                mcpHost?.IsRunning != true,
                ShutdownRequested).Run();
            state.Complete(resultPath);
            Console.WriteLine($"RenderBench completed: {resultPath}");

            if (mcpHost is null || !options.WaitForMcpStart)
                return 0;

            mcpHost.Start();
            await state.ShutdownRequested.ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            state.SetPhase(RenderBenchPhase.Stopping);
            return 0;
        }
        catch (Exception exception)
        {
            state.Fail(exception);
            Console.Error.WriteLine(exception);
            if (mcpHost is not null && options.WaitForMcpStart)
            {
                if (!mcpHost.IsRunning)
                    mcpHost.Start();
                await state.ShutdownRequested.ConfigureAwait(false);
            }
            return 1;
        }
    }
}
