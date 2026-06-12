using ImGuiNET;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor.Mcp;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using XREngine.Rendering.UI;


namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private async Task<ToolCallResult> ExecuteMcpToolCallAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        LogAiTrace(
            $"""
[MCP Assistant Tool Call Request]
tool={toolName}
argumentsLength={argumentsJson?.Length ?? 0}
argumentsStart
{argumentsJson}
argumentsEnd
""");

        string normalizedArgumentsJson = argumentsJson ?? "{}";

        if (string.Equals(toolName, "file_search", StringComparison.OrdinalIgnoreCase))
        {
            string localResult = await ExecuteLocalFileSearchToolAsync(normalizedArgumentsJson, ct);
            LogAiTrace(
                $"""
[MCP Assistant Tool Call Local Result]
tool={toolName}
resultLength={localResult.Length}
resultStart
{localResult}
resultEnd
""");
            return new ToolCallResult(localResult);
        }

        if (string.Equals(toolName, "apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            string localResult = await ExecuteLocalApplyPatchToolAsync(normalizedArgumentsJson, ct);
            LogAiTrace(
                $"""
[MCP Assistant Tool Call Local Result]
tool={toolName}
resultLength={localResult.Length}
resultStart
{localResult}
resultEnd
""");
            return new ToolCallResult(localResult);
        }

        ToolCallResult FinalizeToolResult(string resultText, string? imagePath = null)
        {
            TryAutoFocusCameraForToolCall(toolName, normalizedArgumentsJson, resultText);

            // If the result references an image file, encode it for multimodal feedback.
            string? resolvedImagePath = imagePath;
            if (resolvedImagePath is null)
                resolvedImagePath = TryExtractImagePath(resultText);

            string? imageBase64 = null;
            if (resolvedImagePath is not null && File.Exists(resolvedImagePath))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(resolvedImagePath);
                    imageBase64 = Convert.ToBase64String(bytes);
                }
                catch { /* Non-critical — model just won't get the image */ }
            }

            return new ToolCallResult(resultText, imageBase64, resolvedImagePath);
        }

        string url = _mcpServerUrl.Trim();

        JsonNode? argsNode;
        try
        {
            argsNode = JsonNode.Parse(normalizedArgumentsJson);
        }
        catch
        {
            argsNode = new JsonObject();
        }

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = $"tool-call-{Guid.NewGuid():N}",
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = argsNode
            }
        };

        string payloadJson = payload.ToJsonString();
        LogAiTrace(
            $"""
[MCP Assistant Tool Call Payload]
tool={toolName}
payloadLength={payloadJson.Length}
payloadStart
{payloadJson}
payloadEnd
""");

        try
        {
            // Apply a 30-second timeout for individual MCP tool calls to prevent indefinite hangs.
            using var toolTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            toolTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var linkedCt = toolTimeout.Token;

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(_mcpAuthToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mcpAuthToken.Trim());

            using HttpResponseMessage response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCt);
            string body = await response.Content.ReadAsStringAsync(linkedCt);

            LogAiTrace(
                $"""
[MCP Assistant Tool Call HTTP Response]
tool={toolName}
status={(int)response.StatusCode}
isSuccess={response.IsSuccessStatusCode}
bodyLength={body.Length}
bodyStart
{body}
bodyEnd
""");

            if (!response.IsSuccessStatusCode)
            {
                string errorText = $"[MCP Error] HTTP {(int)response.StatusCode}: {body}";
                LogAiTrace($"[MCP Assistant Tool Call Final Result] tool={toolName} isError=true text={errorText}");
                return new ToolCallResult(errorText);
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("error", out JsonElement errEl) && errEl.ValueKind == JsonValueKind.Object)
            {
                string? errMsg = errEl.TryGetProperty("message", out var m) ? m.GetString() : null;
                string errorText = $"[MCP Error] {errMsg ?? "Unknown error"}";
                LogAiTrace($"[MCP Assistant Tool Call Final Result] tool={toolName} isError=true text={errorText}");
                return new ToolCallResult(errorText);
            }

            if (root.TryGetProperty("result", out JsonElement resultEl))
            {
                // Try to extract text from content array: { "content": [ { "type": "text", "text": "..." } ] }
                var sb = new StringBuilder();
                string? dataImagePath = null;
                if (resultEl.TryGetProperty("content", out JsonElement contentArr) && contentArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in contentArr.EnumerateArray())
                    {
                        if (item.TryGetProperty("text", out JsonElement textEl) && textEl.ValueKind == JsonValueKind.String)
                        {
                            if (sb.Length > 0) sb.Append('\n');
                            sb.Append(textEl.GetString());
                        }
                    }
                }

                // The structured data is already embedded as a JSON text block inside the
                // `content` array by the server, so it has been captured above. We still
                // inspect the legacy top-level `data` field purely to detect an image path
                // (e.g. screenshot results include "path") for multimodal feedback.
                if (resultEl.TryGetProperty("data", out JsonElement dataEl)
                    && dataEl.ValueKind == JsonValueKind.Object
                    && dataEl.TryGetProperty("path", out var pathEl)
                    && pathEl.ValueKind == JsonValueKind.String)
                {
                    string? p = pathEl.GetString();
                    if (p is not null && IsImageFilePath(p))
                        dataImagePath = p;
                }

                if (sb.Length > 0)
                {
                    ToolCallResult finalResult = FinalizeToolResult(sb.ToString(), dataImagePath);
                    LogAiTrace(
                        $"""
[MCP Assistant Tool Call Final Result]
tool={toolName}
isError={finalResult.IsError}
hasImage={finalResult.HasImage}
resultLength={finalResult.Text.Length}
resultStart
{finalResult.Text}
resultEnd
""");
                    return finalResult;
                }

                // Fallback: serialize the entire result
                ToolCallResult fallbackResult = FinalizeToolResult(resultEl.GetRawText(), dataImagePath);
                LogAiTrace(
                    $"""
[MCP Assistant Tool Call Final Result]
tool={toolName}
isError={fallbackResult.IsError}
hasImage={fallbackResult.HasImage}
resultLength={fallbackResult.Text.Length}
resultStart
{fallbackResult.Text}
resultEnd
""");
                return fallbackResult;
            }

            ToolCallResult rawBodyResult = FinalizeToolResult(body);
            LogAiTrace(
                $"""
[MCP Assistant Tool Call Final Result]
tool={toolName}
isError={rawBodyResult.IsError}
hasImage={rawBodyResult.HasImage}
resultLength={rawBodyResult.Text.Length}
resultStart
{rawBodyResult.Text}
resultEnd
""");
            return rawBodyResult;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            string timeoutMessage = $"[MCP Error] Tool call '{toolName}' timed out after 30 seconds.";
            LogAiTrace($"[MCP Assistant Tool Call Timeout] tool={toolName} message={timeoutMessage}");
            return new ToolCallResult(timeoutMessage);
        }
        catch (Exception ex)
        {
            LogAiTrace(
                $"""
[MCP Assistant Tool Call Exception]
tool={toolName}
exception={ex}
""");
            return new ToolCallResult($"[MCP Error] {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if a file path points to a common image format.
    /// </summary>
    private static bool IsImageFilePath(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tries to find an image file path in a tool result text.
    /// Looks for common patterns like absolute paths ending in image extensions.
    /// </summary>
    private static string? TryExtractImagePath(string text)
    {
        // Look for Windows absolute paths (e.g. C:\...\file.png or D:\...\file.jpg)
        int searchStart = 0;
        while (searchStart < text.Length)
        {
            // Find a drive letter pattern
            int driveIdx = text.IndexOf(":\\", searchStart, StringComparison.Ordinal);
            if (driveIdx < 1) break;

            int pathStart = driveIdx - 1;
            if (!char.IsLetter(text[pathStart]))
            {
                searchStart = driveIdx + 2;
                continue;
            }

            // Walk forward to find the end of the path
            int pathEnd = pathStart;
            for (int i = driveIdx + 2; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\'' || c == '\n' || c == '\r' || c == ',' || c == '}' || c == ']' || c == ' ')
                    break;
                pathEnd = i;
            }

            string candidate = text[pathStart..(pathEnd + 1)];
            if (IsImageFilePath(candidate))
                return candidate;

            searchStart = pathEnd + 1;
        }

        return null;
    }

    private void TryAutoFocusCameraForToolCall(string toolName, string argumentsJson, string toolResult)
    {
        if (!AutoCameraView)
            return;

        if (!IsLikelySceneMutationTool(toolName))
            return;

        if (toolResult.StartsWith("[MCP Error]", StringComparison.Ordinal))
            return;

        HashSet<Guid> candidateNodeIds = CollectCandidateSceneNodeIds(argumentsJson, toolResult);
        if (candidateNodeIds.Count == 0)
            return;

        static List<SceneNode> ResolveCandidateNodes(HashSet<Guid> ids)
        {
            List<SceneNode> nodes = [];
            XRWorldInstance? activeWorld = McpWorldResolver.TryGetActiveWorldInstance();
            foreach (Guid id in ids)
            {
                if (!XRObjectBase.ObjectsCache.TryGetValue(id, out var obj) || obj is not SceneNode node)
                    continue;

                if (activeWorld is not null && node.World != activeWorld)
                    continue;

                nodes.Add(node);
            }

            return nodes;
        }

        void FocusCamera()
        {
            List<SceneNode> targetNodes = ResolveCandidateNodes(candidateNodeIds);
            if (targetNodes.Count == 0)
                return;

            if (Engine.State.MainPlayer?.ControlledPawnComponent is not EditorFlyingCameraPawnComponent pawn)
                return;

            pawn.FocusOnNodes(targetNodes, AutoCameraViewFocusDurationSeconds);
        }

        Engine.InvokeOnMainThread(FocusCamera, "MCP Assistant: Auto camera view", executeNowIfAlreadyMainThread: true);
    }

    private static bool IsLikelySceneMutationTool(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        string name = toolName.Trim();

        if (name.Contains("focus_node", StringComparison.OrdinalIgnoreCase))
            return false;

        if (name.StartsWith("list_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("get_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("read_", StringComparison.OrdinalIgnoreCase)
            || name.Contains("query", StringComparison.OrdinalIgnoreCase)
            || name.Contains("search", StringComparison.OrdinalIgnoreCase)
            || name.Contains("inspect", StringComparison.OrdinalIgnoreCase)
            || name.Contains("validate", StringComparison.OrdinalIgnoreCase)
            || name.Contains("debug", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] mutatingHints =
        [
            "create", "add", "spawn", "insert", "update", "set", "change", "modify",
            "move", "rotate", "scale", "reparent", "rename", "delete", "remove", "duplicate",
            "material", "component", "prefab", "attach", "detach"
        ];

        return mutatingHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<Guid> CollectCandidateSceneNodeIds(string argumentsJson, string toolResult)
    {
        var ids = new HashSet<Guid>();

        CollectGuidsFromText(argumentsJson, ids);
        CollectGuidsFromText(toolResult, ids);

        return ids;
    }

    private static void CollectGuidsFromText(string text, HashSet<Guid> ids)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (Match match in GuidPattern.Matches(text))
        {
            if (Guid.TryParse(match.Value, out Guid parsed))
                ids.Add(parsed);
        }
    }

    private static async Task<string> ExecuteLocalFileSearchToolAsync(string argumentsJson, CancellationToken ct)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            JsonElement root = doc.RootElement;

            string query = root.TryGetProperty("query", out var queryEl) && queryEl.ValueKind == JsonValueKind.String
                ? queryEl.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(query))
                return JsonSerializer.Serialize(new { ok = false, error = "Missing required 'query' argument." });

            string pattern = root.TryGetProperty("pattern", out var patternEl) && patternEl.ValueKind == JsonValueKind.String
                ? patternEl.GetString() ?? string.Empty
                : string.Empty;

            string searchRoot = root.TryGetProperty("root", out var rootEl) && rootEl.ValueKind == JsonValueKind.String
                ? rootEl.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(searchRoot))
                searchRoot = Environment.CurrentDirectory;

            int maxResults = root.TryGetProperty("max_results", out var maxEl) && maxEl.TryGetInt32(out int parsed)
                ? Math.Clamp(parsed, 1, 200)
                : 25;

            if (!Directory.Exists(searchRoot))
                return JsonSerializer.Serialize(new { ok = false, error = $"Search root does not exist: {searchRoot}" });

            var results = new List<object>();
            var ignoredSegments = new[] { "\\.git\\", "\\bin\\", "\\obj\\", "\\node_modules\\", "\\Build\\Submodules\\" };

            foreach (string filePath in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                string normalized = filePath.Replace('/', '\\');
                if (ignoredSegments.Any(seg => normalized.Contains(seg, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (!MatchesSimpleGlob(Path.GetFileName(filePath), pattern))
                    continue;

                int lineNumber = 0;
                string? firstHit = null;
                int firstHitLine = 0;

                foreach (string line in File.ReadLines(filePath))
                {
                    ct.ThrowIfCancellationRequested();
                    lineNumber++;

                    if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        firstHit = line.Trim();
                        firstHitLine = lineNumber;
                        break;
                    }
                }

                if (firstHit is null)
                    continue;

                string relativePath = Path.GetRelativePath(searchRoot, filePath).Replace('\\', '/');
                results.Add(new
                {
                    path = relativePath,
                    line = firstHitLine,
                    preview = Truncate(firstHit, 180)
                });

                if (results.Count >= maxResults)
                    break;
            }

            return JsonSerializer.Serialize(new
            {
                ok = true,
                root = searchRoot,
                query,
                pattern,
                count = results.Count,
                results
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
    }

    private static async Task<string> ExecuteLocalApplyPatchToolAsync(string argumentsJson, CancellationToken ct)
    {
        string? tempPatchPath = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            JsonElement root = doc.RootElement;

            string patch = root.TryGetProperty("patch", out var patchEl) && patchEl.ValueKind == JsonValueKind.String
                ? patchEl.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(patch))
                return JsonSerializer.Serialize(new { ok = false, error = "Missing required 'patch' argument." });

            string workRoot = root.TryGetProperty("root", out var rootEl) && rootEl.ValueKind == JsonValueKind.String
                ? rootEl.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(workRoot))
                workRoot = Environment.CurrentDirectory;

            bool dryRun = root.TryGetProperty("dry_run", out var dryEl)
                && dryEl.ValueKind == JsonValueKind.True;

            if (!Directory.Exists(workRoot))
                return JsonSerializer.Serialize(new { ok = false, error = $"Working directory does not exist: {workRoot}" });

            tempPatchPath = Path.Combine(Path.GetTempPath(), $"xrengine_patch_{Guid.NewGuid():N}.diff");
            await File.WriteAllTextAsync(tempPatchPath, patch, ct);

            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = workRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("apply");
            if (dryRun)
                psi.ArgumentList.Add("--check");
            psi.ArgumentList.Add("--whitespace=nowarn");
            psi.ArgumentList.Add(tempPatchPath);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Failed to start git process." });

            string stdout = await process.StandardOutput.ReadToEndAsync(ct);
            string stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            bool ok = process.ExitCode == 0;
            return JsonSerializer.Serialize(new
            {
                ok,
                dry_run = dryRun,
                exit_code = process.ExitCode,
                stdout,
                stderr
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPatchPath) && File.Exists(tempPatchPath))
            {
                try { File.Delete(tempPatchPath); } catch { }
            }
        }
    }

    private static bool MatchesSimpleGlob(string fileName, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return true;

        string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(fileName, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool TryExtractGeneratedImagePath(JsonElement root, out string? imagePath)
    {
        imagePath = null;
        try
        {
            if (!root.TryGetProperty("response", out JsonElement responseEl))
                return false;

            if (!responseEl.TryGetProperty("output", out JsonElement outputEl) || outputEl.ValueKind != JsonValueKind.Array)
                return false;

            foreach (JsonElement item in outputEl.EnumerateArray())
            {
                if (!TryFindFirstBase64Image(item, out string? b64) || string.IsNullOrWhiteSpace(b64))
                    continue;

                string folder = Path.Combine(Environment.CurrentDirectory, "McpCaptures", "GeneratedImages");
                Directory.CreateDirectory(folder);
                string filePath = Path.Combine(folder, $"Generated_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
                File.WriteAllBytes(filePath, Convert.FromBase64String(b64));
                imagePath = filePath;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryFindFirstBase64Image(JsonElement element, out string? base64)
    {
        base64 = null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in element.EnumerateObject())
            {
                if (prop.NameEquals("b64_json") && prop.Value.ValueKind == JsonValueKind.String)
                {
                    base64 = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(base64))
                        return true;
                }

                if (TryFindFirstBase64Image(prop.Value, out base64))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                if (TryFindFirstBase64Image(child, out base64))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Helper record for collecting function calls from SSE events during streaming.
}
