using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>Returns bounded shader command coverage collected from engine-owned backend calls.</summary>
    [XRMcp(Name = "get_shader_command_coverage", Permission = McpPermissionLevel.ReadOnly)]
    [Description("Return opt-in shader command coverage. OpenGL counters are issued commands; Vulkan counters are command-buffer Recorded commands, never GPU-completed work.")]
    public static Task<McpToolResponse> GetShaderCommandCoverageAsync()
    {
        ShaderCommandCoverageSnapshot snapshot = ShaderCommandCoverage.CaptureSnapshot();
        return Task.FromResult(new McpToolResponse(
            "Retrieved shader command coverage.",
            new
            {
                enabled = snapshot.Enabled,
                capacity = snapshot.Capacity,
                dropped_registrations = snapshot.DroppedRegistrations,
                exclusions = ShaderCommandCoverageSnapshot.Exclusions,
                entries = snapshot.Entries,
            }));
    }

    /// <summary>Changes whether future engine-issued shader commands are counted.</summary>
    [XRMcp(Name = "configure_shader_command_coverage", Permission = McpPermissionLevel.Mutate)]
    [Description("Enable or disable bounded shader command coverage. It is disabled by default.")]
    public static Task<McpToolResponse> ConfigureShaderCommandCoverageAsync(
        [McpName("enabled"), Description("Whether to count future commands.")] bool enabled)
    {
        ShaderCommandCoverage.Configure(enabled);
        return Task.FromResult(new McpToolResponse(
            enabled ? "Enabled shader command coverage." : "Disabled shader command coverage.",
            new { enabled }));
    }

    /// <summary>Clears counters while preserving cached linked-program metadata tokens.</summary>
    [XRMcp(Name = "reset_shader_command_coverage", Permission = McpPermissionLevel.Mutate)]
    [Description("Reset shader command counters without invalidating linked-program metadata tokens.")]
    public static Task<McpToolResponse> ResetShaderCommandCoverageAsync()
    {
        ShaderCommandCoverage.Reset();
        return Task.FromResult(new McpToolResponse("Reset shader command coverage counters."));
    }
}
