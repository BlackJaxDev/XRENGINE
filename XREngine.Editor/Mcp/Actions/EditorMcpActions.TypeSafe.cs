using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using XREngine.AgentOrchestration.TypeSafe;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    private static readonly Lazy<ITypeSafeClient> s_typeSafeClient = new(() => new TypeSafeClient());

    /// <summary>
    /// Reports whether the optional TypeSafe System One (Jev) AI integration is configured and available.
    /// </summary>
    [XRMcp(Name = "get_typesafe_status", Permission = McpPermissionLevel.ReadOnly)]
    [Description("Reports whether the optional TypeSafe System One (Jev) AI integration is configured and available.")]
    public static Task<McpToolResponse> GetTypeSafeStatusAsync(McpToolContext context)
    {
        ITypeSafeClient client = s_typeSafeClient.Value;
        bool hasEnvKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.TypeSafeApiKey));

        return Task.FromResult(new McpToolResponse("TypeSafe status retrieved.", new
        {
            configured = hasEnvKey,
            available = client.IsAvailable,
            model = client.TargetModel,
            endpoint = TypeSafeClient.DefaultEndpoint,
            statusMessage = client.IsAvailable
                ? "TypeSafe Jev is configured and ready for semantic evaluation."
                : "TypeSafe Jev is an optional feature. TYPESAFE_API_KEY environment variable is not set; deterministic fallbacks are active.",
        }));
    }

    /// <summary>
    /// Resolves a natural language instruction into a suggested MCP command and target parameters
    /// using TypeSafe Jev when available, with automatic deterministic fallback.
    /// </summary>
    [XRMcp(Name = "resolve_natural_language_scene_command", Permission = McpPermissionLevel.ReadOnly)]
    [Description("Resolves a natural language instruction (e.g. 'focus on main light', 'select player node') into a suggested MCP command and target parameters using TypeSafe Jev if configured, or deterministic pattern matching.")]
    public static async Task<McpToolResponse> ResolveNaturalLanguageSceneCommandAsync(
        McpToolContext context,
        [McpName("instruction"), Description("Natural language instruction to interpret.")] string instruction,
        [McpName("scene_name"), Description("Optional scene name to query nodes from.")] string? sceneName = null)
    {
        if (string.IsNullOrWhiteSpace(instruction))
            return new McpToolResponse("Instruction must not be empty.", isError: true);

        var scene = ResolveScene(context.World, sceneName);
        var nodeCandidates = new List<(string Id, string Name)>();
        if (scene != null)
        {
            foreach (var root in scene.RootNodes)
            {
                if (root == null) continue;
                CollectCandidateNodes(root, nodeCandidates, 0, maxDepth: 4, maxCount: 30);
            }
        }

        ITypeSafeClient client = s_typeSafeClient.Value;
        if (client.IsAvailable)
        {
            try
            {
                var questions = new Dictionary<string, TypeSafeQuestion>
                {
                    ["intent"] = new ChoiceQuestion
                    {
                        Instructions = "What primary scene operation does this user instruction express?",
                        Criteria = new Dictionary<string, string?>
                        {
                            ["focus_node"] = "Move, focus, or look at a specific node or object in the viewport",
                            ["select_node"] = "Select or pick a scene node in the hierarchy or inspector",
                            ["find_nodes"] = "Find, search, or list nodes in the scene",
                            ["inspect_node"] = "Get detailed information or transform of a node",
                            ["unrecognized"] = "Instruction does not match standard editor scene operations",
                        },
                    },
                };

                if (nodeCandidates.Count > 0)
                {
                    var nodeCriteria = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var n in nodeCandidates)
                    {
                        if (!string.IsNullOrWhiteSpace(n.Name) && !string.Equals(n.Name, "none", StringComparison.OrdinalIgnoreCase))
                        {
                            nodeCriteria.TryAdd(n.Name, $"Scene node with name '{n.Name}' and ID {n.Id}");
                            if (nodeCriteria.Count >= 20)
                                break;
                        }
                    }
                    nodeCriteria["none"] = "No specific scene node mentioned";

                    if (nodeCriteria.Count > 1)
                    {
                        questions["target_node"] = new ChoiceQuestion
                        {
                            Instructions = "Which candidate scene node is the user referring to, if any?",
                            Criteria = nodeCriteria,
                        };
                    }
                }

                var evalResponse = await client.EvaluateAsync(
                    state: new
                    {
                        instruction = instruction.Trim(),
                        activeScene = scene?.Name ?? "Default",
                        availableNodes = nodeCandidates
                            .Where(n => !string.IsNullOrWhiteSpace(n.Name))
                            .Select(n => n.Name)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                    },
                    questions: questions).ConfigureAwait(false);

                if (evalResponse != null && evalResponse.Answers.TryGetValue("intent", out var intentAns) && intentAns is ChoiceAnswer choiceIntent)
                {
                    string targetNodeName = string.Empty;
                    if (evalResponse.Answers.TryGetValue("target_node", out var nodeAns) && nodeAns is ChoiceAnswer choiceNode && choiceNode.Choice != "none")
                    {
                        targetNodeName = choiceNode.Choice;
                    }

                    if (choiceIntent.Choice != "unrecognized")
                    {
                        var (toolName, toolArgs) = BuildSuggestedToolCall(choiceIntent.Choice, targetNodeName, nodeCandidates);
                        return new McpToolResponse("Resolved instruction via TypeSafe Jev.", new
                        {
                            source = "typesafe_jev",
                            intent = choiceIntent.Choice,
                            confidence = choiceIntent.Confidence,
                            suggestedTool = toolName,
                            arguments = toolArgs,
                        });
                    }
                }
            }
            catch
            {
                // Fall back to deterministic matching on any evaluation failure.
            }
        }

        // ── Deterministic Fallback ──────────────────────────────────────────
        return ResolveDeterministicFallback(instruction, nodeCandidates);
    }

    private static void CollectCandidateNodes(SceneNode node, List<(string Id, string Name)> list, int depth, int maxDepth, int maxCount)
    {
        if (list.Count >= maxCount || depth > maxDepth)
            return;

        list.Add((node.ID.ToString(), node.Name ?? string.Empty));
        foreach (var child in GetChildren(node))
            if (child != null)
                CollectCandidateNodes(child, list, depth + 1, maxDepth, maxCount);
    }

    private static (string ToolName, object Arguments) BuildSuggestedToolCall(
        string intent,
        string targetNodeName,
        List<(string Id, string Name)> nodeCandidates)
    {
        string? targetNodeId = nodeCandidates
            .FirstOrDefault(n => string.Equals(n.Name, targetNodeName, StringComparison.OrdinalIgnoreCase)).Id;

        return intent switch
        {
            "focus_node" => (
                "focus_node_in_view",
                targetNodeId != null ? (object)new { node_id = targetNodeId } : new { node_name = targetNodeName }
            ),
            "select_node" => (
                "select_node_by_name",
                new { name = targetNodeName }
            ),
            "inspect_node" => (
                "get_scene_node_info",
                targetNodeId != null ? (object)new { node_id = targetNodeId } : new { node_name = targetNodeName }
            ),
            "find_nodes" => (
                "find_nodes_by_name",
                new { query = targetNodeName }
            ),
            _ => ("list_scene_nodes", new { })
        };
    }

    private static McpToolResponse ResolveDeterministicFallback(
        string instruction,
        List<(string Id, string Name)> nodeCandidates)
    {
        string lower = instruction.ToLowerInvariant();
        string intent = "find_nodes";
        string toolName = "find_nodes_by_name";

        if (Regex.IsMatch(lower, @"\b(focus|look at|center|frame)\b"))
        {
            intent = "focus_node";
            toolName = "focus_node_in_view";
        }
        else if (Regex.IsMatch(lower, @"\b(select|pick|highlight)\b"))
        {
            intent = "select_node";
            toolName = "select_node_by_name";
        }
        else if (Regex.IsMatch(lower, @"\b(inspect|info|properties|details)\b"))
        {
            intent = "inspect_node";
            toolName = "get_scene_node_info";
        }

        // Search for any candidate node name substring in instruction
        string matchedNodeName = string.Empty;
        string? matchedNodeId = null;
        foreach (var (id, name) in nodeCandidates)
        {
            if (!string.IsNullOrWhiteSpace(name) && lower.Contains(name.ToLowerInvariant()))
            {
                matchedNodeName = name;
                matchedNodeId = id;
                break;
            }
        }

        object args = toolName switch
        {
            "focus_node_in_view" => matchedNodeId != null
                ? new { node_id = matchedNodeId }
                : (object)new { node_name = matchedNodeName },
            "select_node_by_name" => new { name = matchedNodeName },
            "get_scene_node_info" => matchedNodeId != null
                ? new { node_id = matchedNodeId }
                : (object)new { node_name = matchedNodeName },
            _ => new { query = matchedNodeName.Length > 0 ? matchedNodeName : instruction.Trim() }
        };

        return new McpToolResponse("Resolved instruction via deterministic fallback.", new
        {
            source = "deterministic_fallback",
            intent,
            confidence = matchedNodeName.Length > 0 ? 0.8 : 0.4,
            suggestedTool = toolName,
            arguments = args,
        });
    }
}
