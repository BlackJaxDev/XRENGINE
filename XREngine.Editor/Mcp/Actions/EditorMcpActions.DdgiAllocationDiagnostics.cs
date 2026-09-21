using System;
using System.ComponentModel;
using System.Threading.Tasks;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.GI.DDGI;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        [XRMcp(Name = "get_ddgi_allocation_scopes", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Return rolling managed-allocation samples for executed DDGI commands. This excludes whole-editor and diagnostics work.")]
        public static Task<McpToolResponse> GetDdgiAllocationScopesAsync(
            [McpName("scope_prefix"), Description("Optional DDGI scope-name prefix. Defaults to 'DDGI.'.")]
            string? scopePrefix = null)
        {
            const string defaultPrefix = "DDGI.";
            string prefix = string.IsNullOrWhiteSpace(scopePrefix) ? defaultPrefix : scopePrefix;
            if (!prefix.StartsWith(defaultPrefix, StringComparison.Ordinal))
                return Task.FromResult(new McpToolResponse("scope_prefix must begin with 'DDGI.'.", isError: true));

            DDGIManagedAllocationScopeSnapshot[] snapshot = DDGIManagedAllocationDiagnostics.CaptureSnapshots();
            int count = 0;
            for (int i = 0; i < snapshot.Length; i++)
                if (snapshot[i].Name.StartsWith(prefix, StringComparison.Ordinal))
                    count++;

            object[] scopes = new object[count];
            int index = 0;
            for (int i = 0; i < snapshot.Length; i++)
            {
                DDGIManagedAllocationScopeSnapshot scope = snapshot[i];
                if (!scope.Name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                scopes[index++] = new
                {
                    name = scope.Name,
                    last_bytes = scope.LastBytes,
                    rolling_average_bytes = scope.AverageBytes,
                    rolling_max_bytes = scope.MaxBytes,
                    sample_count = scope.Samples,
                    rolling_window_size = scope.Capacity,
                    rolling_window_full = scope.Samples >= scope.Capacity,
                    lifetime_over_budget_count = scope.OverBudgetCount,
                };
            }

            return Task.FromResult(new McpToolResponse(
                "Retrieved rolling DDGI managed-allocation scopes.",
                new
                {
                    tracking_enabled = RuntimeRenderingHostServices.Profiling.EnableThreadAllocationTracking,
                    aggregation = "Per DDGI command type across all active pipeline instances and views; stereo passes contribute one command execution, not one sample per eye.",
                    scope_prefix = prefix,
                    scope_count = scopes.Length,
                    scopes,
                }));
        }
    }
}
