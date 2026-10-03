using System.ComponentModel;
using XREngine.Data.Core;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    [XRMcp(Name = "set_network_runtime_measurements", Permission = McpPermissionLevel.Mutate)]
    [Description("Enable allocation-free networking measurement scopes. Counters are cumulative; subtract snapshots after warmup.")]
    public static Task<McpToolResponse> SetNetworkRuntimeMeasurementsAsync([McpName("enabled")] bool enabled)
    {
        if (Engine.Networking is not { } manager)
            return Task.FromResult(new McpToolResponse("Networking is not running.", isError: true));
        manager.SetRuntimeMeasurementsEnabled(enabled);
        return GetNetworkRuntimeMeasurementsAsync();
    }

    [XRMcp(Name = "get_network_runtime_measurements", Permission = McpPermissionLevel.ReadOnly)]
    [Description("Read high-rate send, receive, pose application, and relay scope counters plus admission and rejection status.")]
    public static Task<McpToolResponse> GetNetworkRuntimeMeasurementsAsync()
    {
        if (Engine.Networking is not { } manager)
            return Task.FromResult(new McpToolResponse("Networking is not running.", isError: true));
        return Task.FromResult(new McpToolResponse("Networking runtime measurements.", new
        {
            role = manager is ServerNetworkingManager ? "server" : "client",
            enabled = manager.HighRateSendMeasurements.Enabled,
            gameplayReady = manager is ClientNetworkingManager client && client.IsGameplayReady,
            connectedPlayers = manager is ServerNetworkingManager server ? server.GetConnectedPlayers().Count : 0,
            send = manager.HighRateSendMeasurements.Snapshot(),
            receive = manager.HighRateReceiveMeasurements.Snapshot(),
            poseApply = manager.PoseApplyMeasurements.Snapshot(),
            poseRelay = manager.PoseRelayMeasurements.Snapshot(),
            rejections = manager.StateChangeRejections,
            scope = "Synchronous caller-thread allocation; receive excludes socket transport, relay includes pose validation and outgoing enqueue. Nested scopes overlap."
        }));
    }
}
