using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Editor.AssetEditors;

public sealed class RenderPipelineInspector : IXRAssetInspector
{
    #region Constants & Fields

    private static readonly Vector4 DirtyBadgeColor = new(0.95f, 0.55f, 0.2f, 1f);
    private static readonly Vector4 CleanBadgeColor = new(0.5f, 0.8f, 0.5f, 1f);
    private static readonly Vector4 WarningColor = new(0.95f, 0.45f, 0.45f, 1f);

    private readonly ConditionalWeakTable<RenderPipeline, EditorState> _stateCache = new();

    #endregion

    #region Public API

    public void DrawInspector(XRAsset asset, HashSet<object> visitedObjects)
    {
        if (asset is not RenderPipeline pipeline)
        {
            UnitTestingWorld.UserInterface.DrawDefaultAssetInspector(asset, visitedObjects);
            return;
        }

        var state = _stateCache.GetValue(pipeline, _ => new EditorState());

        DrawHeader(pipeline);

        bool drewInstances = DrawInstancesSection(pipeline);
        if (drewInstances)
            ImGui.Separator();

        bool drewPasses = DrawPassMetadataSection(pipeline, state);
        if (drewPasses)
            ImGui.Separator();

        DrawCommandChainSection(pipeline, state, visitedObjects);
        ImGui.Separator();

        DrawDebugViews(pipeline, state);
        ImGui.Separator();

        DrawRawInspector(pipeline, visitedObjects);
    }

    #endregion

    #region Section Drawing - Header

    private static void DrawHeader(RenderPipeline pipeline)
    {
        string displayName = GetDisplayName(pipeline);
        ImGui.TextUnformatted(displayName);

        string path = pipeline.FilePath ?? "<unsaved asset>";
        ImGui.TextDisabled(path);
        if (!string.IsNullOrWhiteSpace(pipeline.FilePath))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy Path##RenderPipeline"))
                ImGui.SetClipboardText(pipeline.FilePath);
        }

        Vector4 statusColor = pipeline.IsDirty ? DirtyBadgeColor : CleanBadgeColor;
        ImGui.TextColored(statusColor, pipeline.IsDirty ? "Modified" : "Saved");

        bool isShadow = pipeline.IsShadowPass;
        if (ImGui.Checkbox("Shadow Pass", ref isShadow))
            pipeline.IsShadowPass = isShadow;

        ImGui.SameLine();
        ImGui.TextDisabled($"Instances: {pipeline.Instances.Count}");

        ImGui.SameLine();
        ImGui.TextDisabled($"Passes: {pipeline.PassMetadata?.Count ?? 0}");

        if (pipeline.CommandChain is null)
        {
            ImGui.TextColored(WarningColor, "Command chain is not initialized.");
            return;
        }

        ImGui.TextDisabled($"Commands: {pipeline.CommandChain.Count}");
        ImGui.SameLine();
        ImGui.TextDisabled($"Branch Resources: {pipeline.CommandChain.BranchResources}");
    }

    #endregion

    #region Section Drawing - Instances

    private static bool DrawInstancesSection(RenderPipeline pipeline)
    {
        if (!ImGui.CollapsingHeader("Active Instances", ImGuiTreeNodeFlags.DefaultOpen))
            return false;

        var instances = pipeline.Instances;
        if (instances.Count == 0)
        {
            ImGui.TextDisabled("Pipeline has no live runtime instances.");
            return true;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("RenderPipelineInstances", 4, flags))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 26f);
            ImGui.TableSetupColumn("Descriptor");
            ImGui.TableSetupColumn("Resources", ImGuiTableColumnFlags.WidthFixed, 180f);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 140f);
            ImGui.TableHeadersRow();

            var activeInstance = Engine.Rendering.State.CurrentRenderingPipeline;
            for (int i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted((i + 1).ToString());

                ImGui.TableSetColumnIndex(1);
                string descriptor = instance.DebugDescriptor;
                ImGui.TextWrapped(descriptor);
                if (ReferenceEquals(activeInstance, instance))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(CleanBadgeColor, "[Active]");
                }

                ImGui.TableSetColumnIndex(2);
                int texCount = instance.Resources.TextureRecords.Count;
                int fboCount = instance.Resources.FrameBufferRecords.Count;
                ImGui.TextDisabled($"{texCount} textures\n{fboCount} FBOs");

                ImGui.TableSetColumnIndex(3);
                if (ImGui.SmallButton($"Purge##RPInstance{instance.GetHashCode():X8}"))
                    instance.DestroyCache();
                ImGui.SameLine();
                if (ImGui.SmallButton($"Copy##RPInstanceCopy{instance.GetHashCode():X8}"))
                    ImGui.SetClipboardText(descriptor);
            }

            ImGui.EndTable();
        }

        return true;
    }

    #endregion

    #region Section Drawing - Debug Views

    private static void DrawDebugViews(RenderPipeline pipeline, EditorState state)
    {
        if (!ImGui.CollapsingHeader("Debug Views (Framebuffers)", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (!Engine.IsRenderThread)
        {
            ImGui.TextDisabled("Preview unavailable off render thread.");
            return;
        }

        var instances = pipeline.Instances;
        if (instances.Count == 0)
        {
            ImGui.TextDisabled("No live pipeline instances to preview.");
            return;
        }

        state.SelectedInstanceIndex = Math.Clamp(state.SelectedInstanceIndex, 0, Math.Max(0, instances.Count - 1));
        string currentInstanceLabel = instances[state.SelectedInstanceIndex].DebugDescriptor;

        ImGui.SetNextItemWidth(360f);
        if (ImGui.BeginCombo("Instance##RenderPipelineFbo", currentInstanceLabel))
        {
            for (int i = 0; i < instances.Count; i++)
            {
                string label = instances[i].DebugDescriptor;
                bool selected = i == state.SelectedInstanceIndex;
                if (ImGui.Selectable(label, selected))
                    state.SelectedInstanceIndex = i;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var selectedInstance = instances[state.SelectedInstanceIndex];
        var fboRecords = selectedInstance.Resources.FrameBufferRecords
            .Where(pair => pair.Value.Instance is not null)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (fboRecords.Count == 0)
        {
            ImGui.TextDisabled("Instance has no framebuffers to preview.");
            return;
        }

        if (string.IsNullOrWhiteSpace(state.SelectedFboName)
            || !fboRecords.Any(pair => pair.Key.Equals(state.SelectedFboName, StringComparison.OrdinalIgnoreCase)))
        {
            state.SelectedFboName = fboRecords[0].Key;
        }

        string currentFboLabel = state.SelectedFboName ?? fboRecords[0].Key;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.BeginCombo("Framebuffer##RenderPipelineFbo", currentFboLabel))
        {
            foreach (var pair in fboRecords)
            {
                bool selected = pair.Key.Equals(state.SelectedFboName, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(pair.Key, selected))
                    state.SelectedFboName = pair.Key;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        bool flipPreview = state.FlipPreview;
        ImGui.Checkbox("Flip Preview Vertically", ref state.FlipPreview);

        var selectedRecord = fboRecords.First(pair => pair.Key.Equals(state.SelectedFboName, StringComparison.OrdinalIgnoreCase));
        XRFrameBuffer fbo = selectedRecord.Value.Instance!;

        ImGui.TextDisabled($"Dimensions: {fbo.Width} x {fbo.Height}");
        ImGui.TextDisabled($"Targets: {fbo.Targets?.Length ?? 0}");
        ImGui.TextDisabled($"Texture Types: {fbo.TextureTypes}");

        var targets = fbo.Targets;
        if (targets is null || targets.Length == 0)
        {
            ImGui.TextDisabled("Framebuffer has no attachments.");
            return;
        }

        for (int i = 0; i < targets.Length; i++)
        {
            var (target, attachment, mipLevel, layerIndex) = targets[i];
            ImGui.PushID(i);

            string attachmentLabel = $"[{i + 1}] {attachment}";
            if (mipLevel > 0)
                attachmentLabel += $" | Mip {mipLevel}";
            if (layerIndex >= 0)
                attachmentLabel += $" | Layer {layerIndex}";
            ImGui.TextUnformatted(attachmentLabel);

            if (target is XRTexture tex)
            {
                string texLabel = tex.Name ?? tex.GetType().Name;
                ImGui.TextDisabled(texLabel);

                if (TryGetTexturePreviewHandle(tex, 320f, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string failure))
                {
                    Vector2 uv0 = flipPreview ? new Vector2(0f, 1f) : Vector2.Zero;
                    Vector2 uv1 = flipPreview ? new Vector2(1f, 0f) : Vector2.One;
                    ImGui.Image(handle, displaySize, uv0, uv1);
                    ImGui.TextDisabled($"Size: {pixelSize.X} x {pixelSize.Y}");
                }
                else
                {
                    ImGui.TextDisabled(failure);
                }
            }
            else
            {
                ImGui.TextDisabled("Attachment is not a texture.");
            }

            ImGui.PopID();
            if (i < targets.Length - 1)
                ImGui.Separator();
        }
    }

    #endregion

    #region Section Drawing - Render Passes

    private static bool DrawPassMetadataSection(RenderPipeline pipeline, EditorState state)
    {
        if (!ImGui.CollapsingHeader("Render Passes", ImGuiTreeNodeFlags.DefaultOpen))
            return false;

        var passes = pipeline.PassMetadata ?? Array.Empty<RenderPassMetadata>();
        if (passes.Count == 0)
        {
            ImGui.TextDisabled("Pass metadata is empty. Trigger a render to populate it.");
            return true;
        }

        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("##RenderPassSearch", "Filter passes...", ref state.PassSearch, 128u);
        string filter = state.PassSearch ?? string.Empty;

        var filtered = FilterPasses(passes, filter).ToList();
        if (filtered.Count == 0)
        {
            ImGui.TextDisabled("No passes matched the filter.");
            return true;
        }

        foreach (var pass in filtered)
        {
            string label = $"[{pass.PassIndex}] {pass.Name}##RenderPass{pass.PassIndex}";
            bool open = ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen);
            ImGui.SameLine();
            ImGui.TextDisabled(pass.Stage.ToString());

            if (!open)
                continue;

            string dependencies = pass.ExplicitDependencies.Count == 0
                ? "<none>"
                : string.Join(", ", pass.ExplicitDependencies);
            ImGui.TextDisabled($"Depends On: {dependencies}");

            if (pass.ResourceUsages.Count == 0)
            {
                ImGui.TextDisabled("No resource usage declared.");
            }
            else if (ImGui.BeginTable($"RenderPassResources{pass.PassIndex}", 5, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("Resource");
                ImGui.TableSetupColumn("Type");
                ImGui.TableSetupColumn("Access", ImGuiTableColumnFlags.WidthFixed, 80f);
                ImGui.TableSetupColumn("Load", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("Store", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableHeadersRow();

                foreach (var usage in pass.ResourceUsages)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(usage.ResourceName);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextDisabled(usage.ResourceType.ToString());
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextDisabled(usage.Access.ToString());
                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextDisabled(usage.LoadOp.ToString());
                    ImGui.TableSetColumnIndex(4);
                    ImGui.TextDisabled(usage.StoreOp.ToString());
                }

                ImGui.EndTable();
            }

            ImGui.TreePop();
        }

        return true;
    }

    #endregion

    #region Section Drawing - Command Chain

    private static void DrawCommandChainSection(RenderPipeline pipeline, EditorState state, HashSet<object> visited)
    {
        if (!ImGui.CollapsingHeader("Command Chain", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var chain = pipeline.CommandChain;
        if (chain is null)
        {
            ImGui.TextDisabled("Pipeline does not define a command chain.");
            return;
        }

        if (chain.Count == 0)
        {
            ImGui.TextDisabled("Pipeline has no commands.");
            return;
        }

        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("##RenderPipelineCommandSearch", "Filter commands...", ref state.CommandSearch, 128u);
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear##RenderPipelineCommandSearch"))
            state.CommandSearch = string.Empty;

        ImGui.Spacing();

        var treeRoot = BuildCommandTree(chain, "Command Chain", "root", out var nodeMap);
        UpdateTreeVisibility(treeRoot, state.CommandSearch);
        EnsureSelectedCommand(state, nodeMap, treeRoot);

        Vector2 avail = ImGui.GetContentRegionAvail();
        float treeHeight = MathF.Max(260f, avail.Y);
        Vector2 treeSize = new(MathF.Max(280f, avail.X * 0.45f), treeHeight);

        using (new ImGuiChildScope("RenderPipelineCommandTree", treeSize))
        {
            bool drewAny = DrawCommandTree(treeRoot, state);
            if (!drewAny)
                ImGui.TextDisabled("No commands matched the filter.");
        }

        ImGui.SameLine();

        using (new ImGuiChildScope("RenderPipelineCommandDetails", new Vector2(0f, treeSize.Y)))
        {
            ViewportRenderCommand? selected = null;
            if (!string.IsNullOrEmpty(state.SelectedCommandPath)
                && nodeMap.TryGetValue(state.SelectedCommandPath, out var selectedNode))
            {
                selected = selectedNode.Command;
            }

            if (selected is null)
            {
                ImGui.TextDisabled("Select a command to inspect.");
                return;
            }

            ImGui.TextUnformatted(selected.GetType().Name);
            var badges = GetCommandBadges(selected);
            if (badges.Count > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, CleanBadgeColor);
                ImGui.TextDisabled(string.Join("  |  ", badges));
                ImGui.PopStyleColor();
            }

            ImGui.TextDisabled($"Executes In Shadow Pass: {selected.ExecuteInShadowPass}");
            ImGui.TextDisabled($"Collect Visible Hook: {selected.NeedsCollecVisible}");

            ImGui.Separator();
            UnitTestingWorld.UserInterface.DrawRuntimeObjectInspector("Command Properties", selected, visited, defaultOpen: true);
        }
    }

    #endregion

    #region Section Drawing - Raw Inspector

    private static void DrawRawInspector(RenderPipeline pipeline, HashSet<object> visited)
    {
        if (!ImGui.CollapsingHeader("Raw Properties"))
            return;

        ImGui.PushID("RenderPipelineRawInspector");
        UnitTestingWorld.UserInterface.DrawDefaultAssetInspector(pipeline, visited);
        ImGui.PopID();
    }

    #endregion

    #region Pass Filtering

    private static IEnumerable<RenderPassMetadata> FilterPasses(IReadOnlyCollection<RenderPassMetadata> passes, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return passes;

        string needle = filter.Trim();
        return passes.Where(pass => PassMatches(pass, needle));
    }

    private static bool PassMatches(RenderPassMetadata pass, string filter)
    {
        if (Contains(pass.Name, filter) || Contains(pass.Stage.ToString(), filter))
            return true;

        foreach (var dep in pass.ExplicitDependencies)
            if (dep.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;

        foreach (var usage in pass.ResourceUsages)
        {
            if (Contains(usage.ResourceName, filter))
                return true;
            if (Contains(usage.ResourceType.ToString(), filter))
                return true;
        }

        return false;
    }

    #endregion

    #region Command Tree - Building & Population

    private static CommandTreeNode BuildCommandTree(ViewportRenderCommandContainer container, string label, string path, out Dictionary<string, CommandTreeNode> nodeMap)
    {
        var root = new CommandTreeNode(label, path, null);
        nodeMap = new Dictionary<string, CommandTreeNode>(StringComparer.Ordinal);
        var visited = new HashSet<ViewportRenderCommandContainer>(ReferenceComparer<ViewportRenderCommandContainer>.Instance);
        PopulateCommandTree(container, root, path, nodeMap, visited);
        return root;
    }

    private static bool PopulateCommandTree(ViewportRenderCommandContainer container, CommandTreeNode parentNode, string parentPath, Dictionary<string, CommandTreeNode> nodeMap, HashSet<ViewportRenderCommandContainer> visited)
    {
        if (container is null)
            return false;

        if (!visited.Add(container))
            return false;

        nodeMap[parentNode.Path] = parentNode;

        for (int i = 0; i < container.Count; i++)
        {
            var command = container[i];
            string commandLabel = $"[{i:000}] {command.GetType().Name}";
            string commandPath = $"{parentPath}/cmd{i:000}";
            var commandNode = new CommandTreeNode(commandLabel, commandPath, command);
            nodeMap[commandPath] = commandNode;

            foreach (var child in EnumerateChildContainers(command, commandPath, container))
            {
                if (!PopulateCommandTree(child.Container, child.Node, child.Node.Path, nodeMap, visited))
                    continue;
                commandNode.Children.Add(child.Node);
            }

            parentNode.Children.Add(commandNode);
        }
        return true;
    }

    private static IEnumerable<ChildContainerInfo> EnumerateChildContainers(ViewportRenderCommand command, string parentPath, ViewportRenderCommandContainer owner)
    {
        var type = command.GetType();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;

            object? value;
            try
            {
                value = property.GetValue(command);
            }
            catch
            {
                continue;
            }

            if (value is null)
                continue;

            if (value is ViewportRenderCommandContainer container)
            {
                if (ReferenceEquals(container, owner) || container.Count == 0)
                    continue;
                yield return CreateChild(property.Name, null, container, parentPath);
                continue;
            }

            if (value is string)
                continue;

            if (value is IEnumerable enumerable)
            {
                int sequenceIndex = 0;
                foreach (var entry in enumerable)
                {
                    switch (entry)
                    {
                        case ViewportRenderCommandContainer nested:
                            if (nested is null || ReferenceEquals(nested, owner) || nested.Count == 0)
                                break;
                            yield return CreateChild(property.Name, sequenceIndex++, nested, parentPath);
                            break;
                        case DictionaryEntry dictEntry when dictEntry.Value is ViewportRenderCommandContainer dictContainer:
                            if (dictContainer is null || ReferenceEquals(dictContainer, owner) || dictContainer.Count == 0)
                                break;
                            yield return CreateChild(property.Name, dictEntry.Key ?? sequenceIndex++, dictContainer, parentPath);
                            break;
                        default:
                            var entryType = entry?.GetType();
                            if (entryType is not null
                                && entryType.IsGenericType
                                && entryType.GetGenericArguments().Length == 2)
                            {
                                object? childContainer = entryType.GetProperty("Value")?.GetValue(entry);
                                object? key = entryType.GetProperty("Key")?.GetValue(entry);
                                if (childContainer is ViewportRenderCommandContainer valueContainer
                                    && !ReferenceEquals(valueContainer, owner)
                                    && valueContainer.Count > 0)
                                    yield return CreateChild(property.Name, key ?? sequenceIndex, valueContainer, parentPath);
                            }
                            sequenceIndex++;
                            break;
                    }
                }
            }
        }

        static ChildContainerInfo CreateChild(string propertyName, object? key, ViewportRenderCommandContainer container, string parentPath)
        {
            string label = FormatContainerLabel(propertyName, key);
            string pathSegment = MakePathSegment(propertyName, key);
            string childPath = $"{parentPath}/{pathSegment}";
            var node = new CommandTreeNode(label, childPath, null);
            return new ChildContainerInfo(node, container);
        }
    }

    private static string FormatContainerLabel(string propertyName, object? key)
    {
        string baseLabel = propertyName switch
        {
            "TrueCommands" => "True Branch",
            "FalseCommands" => "False Branch",
            "DefaultCase" => "Default Case",
            _ => SplitPascalCase(propertyName).Trim()
        };

        if (baseLabel.EndsWith("Commands", StringComparison.OrdinalIgnoreCase))
            baseLabel = baseLabel[..^"Commands".Length].Trim();
        if (baseLabel.Length == 0)
            baseLabel = propertyName;

        if (key is not null)
            baseLabel = string.Concat(baseLabel, " [", key, "]");

        return baseLabel;
    }

    private static string SplitPascalCase(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var builder = new StringBuilder(input.Length * 2);
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (i > 0 && char.IsUpper(c) && !char.IsWhiteSpace(input[i - 1]))
                builder.Append(' ');
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static string MakePathSegment(string propertyName, object? key)
    {
        string baseSegment = key is null
            ? propertyName
            : $"{propertyName}_{key}";

        var builder = new StringBuilder(baseSegment.Length);
        foreach (char c in baseSegment)
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        return builder.ToString();
    }

    #endregion

    #region Command Tree - Visibility & Selection

    private static void EnsureSelectedCommand(EditorState state, Dictionary<string, CommandTreeNode> nodeMap, CommandTreeNode root)
    {
        var firstVisible = FindFirstVisibleCommandNode(root);
        if (firstVisible is null)
        {
            state.SelectedCommandPath = null;
            return;
        }

        if (!string.IsNullOrEmpty(state.SelectedCommandPath)
            && nodeMap.TryGetValue(state.SelectedCommandPath, out var current)
            && current.Command is not null
            && current.IsVisible)
            return;

        state.SelectedCommandPath = firstVisible.Path;
    }

    private static CommandTreeNode? FindFirstVisibleCommandNode(CommandTreeNode node)
    {
        foreach (var child in node.Children)
        {
            if (!child.IsVisible)
                continue;

            if (child.Command is not null)
                return child;

            var nested = FindFirstVisibleCommandNode(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static void UpdateTreeVisibility(CommandTreeNode node, string filter)
    {
        bool matches = string.IsNullOrWhiteSpace(filter)
            || node.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (node.Command is not null && CommandMatches(node.Command, filter));

        bool childVisible = false;
        foreach (var child in node.Children)
        {
            UpdateTreeVisibility(child, filter);
            childVisible |= child.IsVisible;
        }

        node.IsVisible = matches || childVisible;
    }

    #endregion

    #region Command Tree - Drawing

    private static bool DrawCommandTree(CommandTreeNode root, EditorState state)
    {
        bool any = false;
        foreach (var child in root.Children)
            any |= DrawCommandTreeNode(child, state);
        return any;
    }

    private static bool DrawCommandTreeNode(CommandTreeNode node, EditorState state)
    {
        if (!node.IsVisible)
            return false;

        bool hasVisibleChildren = node.Children.Any(child => child.IsVisible);
        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.SpanAvailWidth;
        if (!hasVisibleChildren)
            flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;

        if (node.Command is not null && state.SelectedCommandPath == node.Path)
            flags |= ImGuiTreeNodeFlags.Selected;

        bool open = true;
        if (hasVisibleChildren)
            open = ImGui.TreeNodeEx(node.Path, flags, node.Label);
        else
            ImGui.TreeNodeEx(node.Path, flags, node.Label);

        if (node.Command is not null && ImGui.IsItemClicked(ImGuiMouseButton.Left))
            state.SelectedCommandPath = node.Path;

        if (hasVisibleChildren && open)
        {
            foreach (var child in node.Children)
                DrawCommandTreeNode(child, state);
            ImGui.TreePop();
        }

        return true;
    }

    #endregion

    #region Command Badges & Inspection

    private static List<string> GetCommandBadges(ViewportRenderCommand command)
    {
        var badges = new List<string>(4);
        badges.Add(command.ExecuteInShadowPass ? "Shadow" : "Main");
        if (command.NeedsCollecVisible)
            badges.Add("Collect Visible");
        if (command is ViewportStateRenderCommandBase)
            badges.Add("State Scope");
        if (HasNestedContainers(command))
            badges.Add("Branch");
        return badges;
    }

    private static bool HasNestedContainers(ViewportRenderCommand command)
    {
        var properties = command.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
        foreach (var property in properties)
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;

            if (typeof(ViewportRenderCommandContainer).IsAssignableFrom(property.PropertyType))
            {
                if (property.GetValue(command) is ViewportRenderCommandContainer container && container.Count > 0)
                    return true;
            }
            else if (typeof(IEnumerable<ViewportRenderCommandContainer>).IsAssignableFrom(property.PropertyType))
            {
                if (property.GetValue(command) is IEnumerable<ViewportRenderCommandContainer> collection)
                {
                    foreach (var nested in collection)
                    {
                        if (nested is not null && nested.Count > 0)
                            return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool CommandMatches(ViewportRenderCommand command, string filter)
    {
        if (Contains(command.GetType().Name, filter))
            return true;

        foreach (var badge in GetCommandBadges(command))
            if (Contains(badge, filter))
                return true;

        return false;
    }

    #endregion

    #region Texture Preview

    private static bool TryGetTexturePreviewHandle(XRTexture texture, float maxEdge, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string failure)
    {
        handle = nint.Zero;
        displaySize = new Vector2(64f, 64f);
        pixelSize = displaySize;
        failure = string.Empty;

        if (!Engine.IsRenderThread)
        {
            failure = "Preview unavailable off render thread";
            return false;
        }

        if (texture is not XRTexture2D tex2D)
        {
            failure = "Only 2D textures supported";
            return false;
        }

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
        {
            failure = "Preview requires OpenGL renderer";
            return false;
        }

        var apiTexture = renderer.GenericToAPI<GLTexture2D>(tex2D);
        if (apiTexture is null)
        {
            failure = "Texture not uploaded";
            return false;
        }

        uint binding = apiTexture.BindingId;
        if (binding == OpenGLRenderer.GLObjectBase.InvalidBindingId || binding == 0)
        {
            failure = "Texture not ready";
            return false;
        }

        pixelSize = new Vector2(tex2D.Width, tex2D.Height);
        float largest = MathF.Max(1f, MathF.Max(pixelSize.X, pixelSize.Y));
        float scale = largest > 0f ? MathF.Min(1f, maxEdge / largest) : 1f;
        displaySize = new Vector2(pixelSize.X * scale, pixelSize.Y * scale);
        handle = (nint)binding;
        return true;
    }

    #endregion

    #region String Utilities

    private static bool Contains(string? haystack, string needle)
        => !string.IsNullOrWhiteSpace(haystack)
           && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string GetDisplayName(RenderPipeline pipeline)
    {
        if (!string.IsNullOrWhiteSpace(pipeline.Name))
            return pipeline.Name!;
        return pipeline.GetType().Name;
    }

    #endregion

    #region Nested Types

    private sealed class EditorState
    {
        public string PassSearch = string.Empty;
        public string CommandSearch = string.Empty;
        public string? SelectedCommandPath;
        public int SelectedInstanceIndex;
        public string? SelectedFboName;
        public bool FlipPreview = true;
    }

    private sealed class CommandTreeNode(string label, string path, ViewportRenderCommand? command)
    {
        public string Label { get; } = label;
        public string Path { get; } = path;
        public ViewportRenderCommand? Command { get; } = command;
        public List<CommandTreeNode> Children { get; } = new();
        public bool IsVisible { get; set; }
    }

    private readonly record struct ChildContainerInfo(CommandTreeNode Node, ViewportRenderCommandContainer Container);

    private sealed class ReferenceComparer<T> : IEqualityComparer<T>
        where T : class
    {
        public static ReferenceComparer<T> Instance { get; } = new();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private readonly struct ImGuiChildScope : IDisposable
    {
        public ImGuiChildScope(string id, Vector2 size)
        {
            ImGui.BeginChild(id, size, ImGuiChildFlags.Border);
        }

        public void Dispose()
        {
            ImGui.EndChild();
        }
    }

    #endregion
}
