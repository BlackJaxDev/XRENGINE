using System.Text.Json;
using XREngine.Runtime.Automation.Mcp;
using XREngine.Runtime.Automation.Profiling;

namespace XREngine.RenderBench;

/// <summary>RenderBench adapter over the shared runtime MCP transport and profile tool bundle.</summary>
public sealed class RenderBenchMcpHost : IAsyncDisposable
{
    private readonly McpHttpServer _server;

    public RenderBenchMcpHost(
        RenderBenchOptions options,
        RenderBenchProcessState state,
        RenderProfileControlService profileService)
    {
        McpToolRegistry registry = new();
        registry.Register(new RenderProfileMcpToolBundle());
        registry.Register(new RenderBenchLifecycleToolBundle(state));
        Dictionary<Type, object> services = new()
        {
            [typeof(RenderProfileControlService)] = profileService,
            [typeof(RenderBenchProcessState)] = state,
        };
        McpToolContext context = new(
            McpCapability.ProfilerSession | McpCapability.Renderer | McpCapability.RenderTarget,
            services);
        _server = new McpHttpServer(
            new McpHttpServerOptions
            {
                Port = options.McpPort,
                ServerName = "XREngine.RenderBench",
                ServerVersion = "3",
                SessionToken = options.SessionToken,
                AllowMutations = options.McpPolicy == RenderBenchMcpPolicy.Control,
                StatusProvider = state.Snapshot,
                ShutdownRequested = () =>
                {
                    state.SetPhase(RenderBenchPhase.Stopping);
                    state.RequestShutdown();
                },
            },
            registry,
            () => context);
        profileService.RunWithTransportSuspendedAsync = _server.RunWithTransportSuspendedAsync;
    }

    public bool IsRunning => _server.IsRunning;
    public void Start() => _server.Start();
    public Task StopAsync() => _server.StopAsync();
    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private sealed class RenderBenchLifecycleToolBundle(RenderBenchProcessState state) : IMcpToolBundle
    {
        private static readonly object s_schema = new
        {
            type = "object",
            properties = new Dictionary<string, object>(),
            additionalProperties = false,
        };

        public IEnumerable<McpToolDefinition> GetTools()
        {
            yield return new McpToolDefinition(
                "get_render_bench_status",
                "Gets the dedicated Vulkan RenderBench process state and result path.",
                s_schema,
                (_, _, _) => Task.FromResult(new McpToolResponse("Retrieved RenderBench status.", state.Snapshot())));
            yield return new McpToolDefinition(
                "start_render_bench",
                "Runs the process-configured deterministic benchmark after the response is serialized.",
                s_schema,
                StartAsync,
                Permission: McpPermissionLevel.Mutating);
            yield return new McpToolDefinition(
                "stop_render_bench",
                "Requests clean shutdown of this RenderBench process.",
                s_schema,
                StopAsync,
                Permission: McpPermissionLevel.Mutating);
        }

        private Task<McpToolResponse> StartAsync(McpToolContext _, JsonElement __, CancellationToken ___)
        {
            if (state.Snapshot().Phase != RenderBenchPhase.Idle)
                return Task.FromResult(new McpToolResponse("RenderBench is not idle.", IsError: true));
            return Task.FromResult(new McpToolResponse(
                "RenderBench start accepted.",
                new { accepted = true },
                AfterResponse: () =>
                {
                    state.RequestStart();
                    return Task.CompletedTask;
                }));
        }

        private Task<McpToolResponse> StopAsync(McpToolContext _, JsonElement __, CancellationToken ___)
        {
            state.SetPhase(RenderBenchPhase.Stopping);
            state.RequestShutdown();
            return Task.FromResult(new McpToolResponse("RenderBench shutdown requested.", new { stopping = true }));
        }
    }
}
