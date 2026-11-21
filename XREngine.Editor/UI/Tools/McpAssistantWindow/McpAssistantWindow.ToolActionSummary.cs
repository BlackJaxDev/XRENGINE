using System.Text.Json;

namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    /// <summary>Builds bounded action text from tool identifiers and reported results.</summary>
    private static class ToolActionSummary
    {
        private static readonly string[] TargetKeys =
        [
            "property_name", "property", "member_name", "member", "symbol", "node_name", "component_name",
            "name", "source_path", "path", "node_id", "component_id", "asset_id", "query"
        ];

        public static string GetTargetLabel(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson.Length > 32_768)
                return string.Empty;

            try
            {
                using JsonDocument document = JsonDocument.Parse(argumentsJson);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return string.Empty;

                foreach (string key in TargetKeys)
                {
                    if (!root.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.String)
                        continue;

                    string? text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        return Truncate(text.Trim(), 80);
                }
            }
            catch (JsonException)
            {
                // An invalid argument block cannot supply a trusted target label.
            }

            return string.Empty;
        }

        public static string GetActionName(string toolName)
        {
            string name = NormalizeName(toolName);
            return name switch
            {
                "capture_viewport_screenshot" or "request_view_screenshot" => "Capture viewport",
                "get_scene_snapshot" or "list_scene_nodes" => "Inspect scene",
                "get_component_property" => "Read property",
                "set_component_property" => "Change property",
                "get_component_snapshot" => "Inspect component",
                "get_component_schema" => "Read component settings",
                "get_node_snapshot" or "get_scene_node_info" => "Inspect object",
                "create_node" or "create_scene_node" => "Create object",
                "delete_node" or "delete_scene_node" => "Delete object",
                "apply_patch" => "Apply file changes",
                "file_search" or "localfile_search" => "Search project files",
                "search_engine_help" => "Search engine help",
                "read_engine_help" => "Read engine source",
                _ when name.Length == 0 => "Editor action",
                _ => char.ToUpperInvariant(name[0]) + name[1..].Replace('_', ' ')
            };
        }

        public static string GetCategory(string toolName)
        {
            string name = NormalizeName(toolName);
            if (name is "search_engine_help" or "read_engine_help"
                || name.Contains("source", StringComparison.Ordinal)
                || name.Contains("symbol", StringComparison.Ordinal)
                || name.Contains("documentation", StringComparison.Ordinal)
                || name.Contains("type_info", StringComparison.Ordinal))
                return "Engine source and help";
            if (name.Contains("screenshot", StringComparison.Ordinal)
                || name.Contains("viewport", StringComparison.Ordinal)
                || name.Contains("camera", StringComparison.Ordinal))
                return "Viewport";
            if (name.Contains("file", StringComparison.Ordinal)
                || name.Contains("asset", StringComparison.Ordinal)
                || name.Contains("script", StringComparison.Ordinal)
                || name.Contains("shader", StringComparison.Ordinal))
                return "Files and assets";
            if (name.Contains("scene", StringComparison.Ordinal)
                || name.Contains("node", StringComparison.Ordinal)
                || name.Contains("component", StringComparison.Ordinal)
                || name.Contains("property", StringComparison.Ordinal))
                return "Scene";
            return "Editor action";
        }

        public static string GetOutcome(string toolName, string result, bool isError, string? imagePath)
        {
            string firstLine = FirstResultLine(result);
            if (isError)
                return firstLine.Length == 0 ? "The action failed." : $"The action failed: {firstLine}";
            if (!string.IsNullOrWhiteSpace(imagePath))
                return "The tool saved an image.";
            if (IsMutation(toolName))
                return firstLine.Length == 0 ? "The tool completed the change request." : firstLine;
            return firstLine.Length == 0 ? "The tool finished." : firstLine;
        }

        public static string GetVerification(string toolName, bool isError)
        {
            if (isError || !IsMutation(toolName))
                return string.Empty;
            return "No verification is linked to this action.";
        }

        /// <summary>Reads a file citation from a successful bounded source-read result.</summary>
        public static void ReadSourceReference(
            string toolName,
            string result,
            out string? path,
            out int? startLine,
            out string? hash,
            out string? revision,
            out string? binaryMatch)
        {
            path = null;
            startLine = null;
            hash = null;
            revision = null;
            binaryMatch = null;
            if (NormalizeName(toolName) != "read_engine_help")
                return;

            int jsonStart = result.IndexOf('{');
            if (jsonStart < 0 || result.Length - jsonStart > 131_072)
                return;

            try
            {
                using JsonDocument document = JsonDocument.Parse(result[jsonStart..]);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("path", out JsonElement pathElement)
                    || pathElement.ValueKind != JsonValueKind.String)
                    return;

                string? candidate = pathElement.GetString();
                if (string.IsNullOrWhiteSpace(candidate) || Path.IsPathRooted(candidate)
                    || candidate.Split('/', '\\').Contains("..", StringComparer.Ordinal))
                    return;

                path = candidate;
                if (root.TryGetProperty("startLine", out JsonElement lineElement)
                    && lineElement.ValueKind == JsonValueKind.Number
                    && lineElement.TryGetInt32(out int lineNumber)
                    && lineNumber > 0)
                    startLine = lineNumber;
                hash = ReadString(root, "sha256");
                revision = ReadString(root, "revision");
                binaryMatch = ReadString(root, "binaryMatch");
            }
            catch (JsonException)
            {
                // A malformed source result does not produce a source link.
            }
        }

        private static string? ReadString(JsonElement root, string name)
            => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        public static bool IsMutation(string toolName)
        {
            string name = NormalizeName(toolName);
            return name.StartsWith("set_", StringComparison.Ordinal)
                || name.StartsWith("create_", StringComparison.Ordinal)
                || name.StartsWith("delete_", StringComparison.Ordinal)
                || name.StartsWith("remove_", StringComparison.Ordinal)
                || name.StartsWith("rename_", StringComparison.Ordinal)
                || name.StartsWith("reparent_", StringComparison.Ordinal)
                || name.StartsWith("apply_", StringComparison.Ordinal)
                || name.StartsWith("write_", StringComparison.Ordinal)
                || name.StartsWith("update_", StringComparison.Ordinal)
                || name.StartsWith("move_", StringComparison.Ordinal)
                || name.StartsWith("add_", StringComparison.Ordinal)
                || name.StartsWith("edit_", StringComparison.Ordinal)
                || name.StartsWith("toggle_", StringComparison.Ordinal)
                || name.StartsWith("instantiate_", StringComparison.Ordinal)
                || name.StartsWith("import_", StringComparison.Ordinal);
        }

        private static string NormalizeName(string toolName)
            => toolName.Trim().Replace(' ', '_').ToLowerInvariant();

        private static string FirstResultLine(string result)
        {
            if (string.IsNullOrWhiteSpace(result))
                return string.Empty;
            string first = result.AsSpan().TrimStart().ToString();
            int newline = first.IndexOfAny('\r', '\n');
            if (newline >= 0)
                first = first[..newline];
            first = first.Trim();
            if (first.StartsWith('{') || first.StartsWith('['))
                return string.Empty;
            return Truncate(first, 180);
        }
    }
}
