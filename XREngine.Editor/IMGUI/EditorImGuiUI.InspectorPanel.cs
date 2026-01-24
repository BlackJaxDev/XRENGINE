using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using ImGuiNET;
using XREngine;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Core.Reflection.Attributes;
using XREngine.Core.Files;
using XREngine.Editor.AssetEditors;
using XREngine.Editor.ComponentEditors;
using XREngine.Editor.TransformEditors;
using XREngine.Rendering.OpenGL;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static string _inspectorPropertySearch = string.Empty;

        private static XRComponent? _componentPendingRename;
        private static bool _componentRenameInputFocusRequested;
        private static readonly byte[] _componentRenameBuffer = new byte[256];

        private static void BeginComponentRename(XRComponent component)
        {
            _componentPendingRename = component;
            _componentRenameInputFocusRequested = true;
            PopulateComponentRenameBuffer(component.Name ?? string.Empty);
        }

        private static void CancelComponentRename()
        {
            _componentPendingRename = null;
            _componentRenameInputFocusRequested = false;
            Array.Clear(_componentRenameBuffer, 0, _componentRenameBuffer.Length);
        }

        private static void ApplyComponentRename()
        {
            if (_componentPendingRename is null)
                return;

            string newName = ExtractStringFromComponentRenameBuffer();
            newName = newName.Trim();
            _componentPendingRename.Name = string.IsNullOrWhiteSpace(newName) ? null : newName;
            CancelComponentRename();
        }

        private static void PopulateComponentRenameBuffer(string source)
        {
            Array.Clear(_componentRenameBuffer, 0, _componentRenameBuffer.Length);
            if (string.IsNullOrEmpty(source))
                return;

            int written = Encoding.UTF8.GetBytes(source, 0, source.Length, _componentRenameBuffer, 0);
            if (written < _componentRenameBuffer.Length)
                _componentRenameBuffer[written] = 0;
        }

        private static string ExtractStringFromComponentRenameBuffer()
        {
            int length = Array.IndexOf(_componentRenameBuffer, (byte)0);
            if (length < 0)
                length = _componentRenameBuffer.Length;

            return Encoding.UTF8.GetString(_componentRenameBuffer, 0, length);
        }

        private readonly record struct ComponentInspectorLabels(string? Header, string? Footer);
        private static readonly Dictionary<Type, ComponentInspectorLabels> _componentInspectorLabelsCache = new();

        private static ComponentInspectorLabels GetComponentInspectorLabels(Type componentType)
        {
            if (_componentInspectorLabelsCache.TryGetValue(componentType, out var cached))
                return cached;

            string? header = componentType.GetCustomAttribute<InspectorHeaderLabelAttribute>(true)?.Text;
            string? footer = componentType.GetCustomAttribute<InspectorFooterLabelAttribute>(true)?.Text;

            var labels = new ComponentInspectorLabels(header, footer);
            _componentInspectorLabelsCache[componentType] = labels;
            return labels;
        }

        private static partial void BeginAddComponentForHierarchyNode(SceneNode node)
        {
            BeginAddComponentForHierarchyNodes(new[] { node });
        }

        private static void BeginAddComponentForHierarchyNodes(IReadOnlyList<SceneNode> nodes)
        {
            _nodesPendingAddComponent = nodes;
            _componentPickerSearch = string.Empty;
            _componentPickerError = null;
            _addComponentPopupOpen = true;
            _addComponentPopupRequested = true;
        }

        private static partial void DrawHierarchyAddComponentPopup()
        {
            if (_nodesPendingAddComponent is null && !_addComponentPopupOpen)
                return;

            if (_addComponentPopupRequested)
            {
                ImGui.OpenPopup(HierarchyAddComponentPopupId);
                _addComponentPopupRequested = false;
            }

            ImGui.SetNextWindowSize(new Vector2(640f, 520f), ImGuiCond.FirstUseEver);
            if (ImGui.BeginPopupModal(HierarchyAddComponentPopupId, ref _addComponentPopupOpen, ImGuiWindowFlags.NoCollapse))
            {
                var targetNodes = _nodesPendingAddComponent;
                if (targetNodes is null || targetNodes.Count == 0)
                {
                    ImGui.CloseCurrentPopup();
                    ImGui.EndPopup();
                    return;
                }

                string title = targetNodes.Count == 1
                    ? $"Add Component to '{targetNodes[0].Name ?? SceneNode.DefaultName}'"
                    : $"Add Component to {targetNodes.Count} Scene Nodes";
                ImGui.TextUnformatted(title);
                ImGui.Spacing();

                if (!string.IsNullOrEmpty(_componentPickerError))
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
                    ImGui.TextWrapped(_componentPickerError);
                    ImGui.PopStyleColor();
                    ImGui.Spacing();
                }

                ImGui.InputText("Search", ref _componentPickerSearch, 256, ImGuiInputTextFlags.AutoSelectAll);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Filter by component name, namespace, or assembly.");

                var filteredTypes = GetFilteredComponentTypes(_componentPickerSearch);

                ImGui.Separator();

                const float componentListHeight = 360.0f;
                Type? requestedComponent = null;

                if (ImGui.BeginChild("ComponentList", new Vector2(0, componentListHeight), ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar))
                {
                    if (filteredTypes.Count == 0)
                    {
                        ImGui.TextDisabled("No components match the current search.");
                    }
                    else
                    {
                        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.NoSavedSettings;
                        if (ImGui.BeginTable("ComponentTable", 3, tableFlags))
                        {
                            ImGui.TableSetupColumn("Component", ImGuiTableColumnFlags.NoHide, 0.4f);
                            ImGui.TableSetupColumn("Namespace", ImGuiTableColumnFlags.NoHide, 0.35f);
                            ImGui.TableSetupColumn("Assembly", ImGuiTableColumnFlags.NoHide, 0.25f);
                            ImGui.TableHeadersRow();

                            foreach (var descriptor in filteredTypes)
                            {
                                ImGui.TableNextRow();
                                ImGui.TableSetColumnIndex(0);
                                string selectableLabel = $"{descriptor.DisplayName}##{descriptor.FullName}";
                                bool selected = ImGui.Selectable(selectableLabel, false, ImGuiSelectableFlags.SpanAllColumns);
                                if (ImGui.IsItemHovered())
                                    ImGui.SetTooltip(descriptor.FullName);
                                if (selected)
                                    requestedComponent = descriptor.Type;

                                ImGui.TableSetColumnIndex(1);
                                ImGui.TextUnformatted(string.IsNullOrEmpty(descriptor.Namespace) ? "<global>" : descriptor.Namespace);

                                ImGui.TableSetColumnIndex(2);
                                ImGui.TextUnformatted(descriptor.AssemblyName);
                            }

                            ImGui.EndTable();
                        }
                    }

                    ImGui.EndChild();
                }

                bool closePopup = false;
                if (requestedComponent is not null)
                {
                    var failedNodes = new List<SceneNode>();
                    foreach (var node in targetNodes)
                    {
                        if (!node.TryAddComponent(requestedComponent, out _))
                            failedNodes.Add(node);
                    }

                    if (failedNodes.Count == 0)
                    {
                        _componentPickerError = null;
                        closePopup = true;
                    }
                    else
                    {
                        string failureList = string.Join(", ", failedNodes.Select(n => n.Name ?? SceneNode.DefaultName));
                        _componentPickerError = $"Unable to add component '{requestedComponent.Name}' to: {failureList}.";
                    }
                }

                if (closePopup)
                    CloseComponentPickerPopup();

                ImGui.Separator();

                if (ImGui.Button("Close") || ImGui.IsKeyPressed(ImGuiKey.Escape))
                    CloseComponentPickerPopup();

                ImGui.EndPopup();
            }
            else if (_nodesPendingAddComponent is not null)
            {
                ResetComponentPickerState();
            }
        }

        private static partial void DrawInspectorPanel()
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawInspectorPanel");
            if (!_showInspector) return;

            if (!ImGui.Begin("Inspector", ref _showInspector))
            {
                ImGui.End();
                return;
            }

            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##InspectorPropertySearch", "Search properties...", ref _inspectorPropertySearch, 256);
            ImGui.Spacing();

            object? standaloneTarget = _inspectorStandaloneTarget;
            var selectedNodes = Selection.SceneNodes;
            SceneNode? fallbackNode = Selection.LastSceneNode;

            if (standaloneTarget is null && selectedNodes.Length == 0 && fallbackNode is null)
            {
                ImGui.TextUnformatted("Select a scene node in the hierarchy to inspect its properties.");
            }
            else
            {
                ImGui.BeginChild("InspectorContent", Vector2.Zero, ImGuiChildFlags.Border);

                bool allowSceneInspector = false;

                if (standaloneTarget is not null)
                {
                    DrawStandaloneInspectorTarget(standaloneTarget);

                    if (_inspectorStandaloneTarget is null && (selectedNodes.Length > 0 || fallbackNode is not null))
                        allowSceneInspector = true;
                }
                else if (selectedNodes.Length > 0 || fallbackNode is not null)
                {
                    allowSceneInspector = true;
                }

                if (allowSceneInspector)
                {
                    if (selectedNodes.Length > 0)
                        DrawSceneNodeInspector(selectedNodes);
                    else if (fallbackNode is not null)
                        DrawSceneNodeInspector(fallbackNode);
                }

                ImGui.EndChild();
            }

            DrawHierarchyAddComponentPopup();

            ImGui.End();
        }

        private static void DrawSceneNodeInspector(IReadOnlyList<SceneNode> nodes)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSceneNodeInspector.Multi");
            if (nodes.Count == 1)
            {
                DrawSceneNodeInspector(nodes[0]);
                return;
            }

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var node in nodes)
                visited.Add(node);

            ImGui.PushID("SceneNodeMultiInspector");

            ImGui.TextUnformatted($"{nodes.Count} Scene Nodes Selected");
            ImGui.TextDisabled("Editing shared values will apply to all selected nodes.");
            ImGui.Separator();

            DrawSceneNodeBasics(nodes);

            ImGui.Separator();

            if (ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.PushID("TransformSection");
                DrawTransformInspector(nodes.Select(n => n.Transform).ToList(), visited);
                ImGui.PopID();
            }

            ImGui.Separator();
            ImGui.TextUnformatted("Components");
            ImGui.SameLine();
            if (ImGui.Button("Add Component to Selected..."))
                BeginAddComponentForHierarchyNodes(nodes);

            ImGui.Spacing();
            DrawComponentInspectors(nodes, visited);

            ImGui.PopID();
        }

        private static partial void DrawSceneNodeInspector(SceneNode node)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSceneNodeInspector");
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance)
            {
                node
            };

            ImGui.PushID(node.ID.ToString());

            DrawSceneNodeBasics(node);

            ImGui.Separator();

            if (ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.PushID("TransformSection");
                DrawTransformInspector(node.Transform, visited);
                ImGui.PopID();
            }

            ImGui.Separator();
            ImGui.TextUnformatted("Components");
            ImGui.SameLine();
            if (ImGui.Button("Add Component..."))
                BeginAddComponentForHierarchyNode(node);

            ImGui.Spacing();
            DrawComponentInspectors(node, visited);

            ImGui.PopID();
        }

        private static void DrawSceneNodeBasics(IReadOnlyList<SceneNode> nodes)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSceneNodeBasics.Multi");
            const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV;
            if (!ImGui.BeginTable("SceneNodeBasicsMulti", 2, tableFlags))
                return;

            DrawInspectorRow("Name", () =>
            {
                string? commonName = nodes.Select(n => n.Name ?? string.Empty).Distinct().Count() == 1
                    ? nodes[0].Name ?? string.Empty
                    : null;

                string name = commonName ?? string.Empty;
                ImGui.SetNextItemWidth(-1f);
                string hint = commonName is null ? "<multiple>" : string.Empty;
                if (ImGui.InputTextWithHint("##SceneNodeNameMulti", hint, ref name, 256))
                {
                    string trimmed = name.Trim();
                    string newName = string.IsNullOrWhiteSpace(trimmed) ? SceneNode.DefaultName : trimmed;
                    foreach (var node in nodes)
                        node.Name = newName;
                }
            });

            DrawInspectorRow("Active Self", () =>
            {
                bool allActive = nodes.All(n => n.IsActiveSelf);
                bool allInactive = nodes.All(n => !n.IsActiveSelf);
                bool mixed = !allActive && !allInactive;
                bool active = allActive;
                if (mixed)
                    ImGui.PushItemFlag(ImGuiItemFlags.MixedValue, true);
                bool toggled = ImGui.Checkbox("##SceneNodeActiveSelfMulti", ref active);
                if (mixed)
                    ImGui.PopItemFlag();
                if (toggled)
                {
                    foreach (var node in nodes)
                        node.IsActiveSelf = active;
                }
            });

            DrawInspectorRow("Active In Hierarchy", () =>
            {
                bool allActive = nodes.All(n => n.IsActiveInHierarchy);
                bool allInactive = nodes.All(n => !n.IsActiveInHierarchy);
                bool mixed = !allActive && !allInactive;
                bool active = allActive;
                if (mixed)
                    ImGui.PushItemFlag(ImGuiItemFlags.MixedValue, true);
                bool toggled = ImGui.Checkbox("##SceneNodeActiveInHierarchyMulti", ref active);
                if (mixed)
                    ImGui.PopItemFlag();
                if (toggled)
                {
                    foreach (var node in nodes)
                        node.IsActiveInHierarchy = active;
                }
            });

            DrawInspectorRow("ID", () =>
            {
                bool same = nodes.Select(n => n.ID).Distinct().Count() == 1;
                ImGui.TextUnformatted(same ? nodes[0].ID.ToString() : "<multiple>");
            });

            DrawInspectorRow("Path", () =>
            {
                string firstPath = nodes[0].GetPath();
                bool same = nodes.All(n => string.Equals(n.GetPath(), firstPath, StringComparison.Ordinal));
                ImGui.PushTextWrapPos();
                ImGui.TextUnformatted(same ? firstPath : "<multiple>");
                ImGui.PopTextWrapPos();
            });

            ImGui.EndTable();
        }

        private static void DrawStandaloneInspectorContent(object target, HashSet<object> visited)
        {
            if (target is ThirdPartyImportSelection thirdParty)
            {
                DrawThirdPartyImportSettings(thirdParty.SourcePath, thirdParty.AssetType, visited);
                return;
            }

            if (target is XRAsset asset)
            {
                DrawThirdPartyImportSettings(asset, visited);
                if (TryDrawAssetInspector(new InspectorTargetSet(new[] { asset }, asset.GetType()), visited))
                    return;
            }

            DrawInspectableObject(new InspectorTargetSet(new[] { target }, target.GetType()), "StandaloneInspectorProperties", visited);
        }

        private static void DrawThirdPartyImportSettings(XRAsset asset, HashSet<object> visited)
        {
            if (asset.FilePath is null)
                return;

            // Only show for the original 3rd-party file selection (not for native .asset files).
            if (string.Equals(Path.GetExtension(asset.FilePath), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
                return;

            var assets = Engine.Assets;
            if (assets is null)
                return;

            if (!assets.TryGetThirdPartyImportContext(asset.FilePath, asset.GetType(), out var importOptions, out var optionsPath, out var generatedAssetPath))
                return;

            if (!ImGui.CollapsingHeader("Import Settings", ImGuiTreeNodeFlags.DefaultOpen))
                return;

            ImGui.PushID("ThirdPartyImportSettings");

            ImGui.TextDisabled("Source");
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(asset.FilePath);
            ImGui.PopTextWrapPos();

            ImGui.TextDisabled("Generated Asset");
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(generatedAssetPath);
            ImGui.PopTextWrapPos();

            ImGui.TextDisabled("Options Cache");
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(optionsPath);
            ImGui.PopTextWrapPos();

            ImGui.Separator();

            DrawInspectableObject(new InspectorTargetSet(new[] { importOptions! }, importOptions!.GetType()), "ThirdPartyImportOptions", visited);

            ImGui.Spacing();
            if (ImGui.Button("Save & Reimport"))
            {
                bool saved = assets.SaveThirdPartyImportOptions(asset.FilePath, asset.GetType(), importOptions!);
                _ = assets.ReimportThirdPartyFileAsync(asset.FilePath).ContinueWith(t =>
                {
                    bool reimported = t.Status == TaskStatus.RanToCompletion && t.Result;
                    Debug.Out($"[ImportSettings] Saved={saved}, Reimported={reimported} for '{asset.FilePath}'.");
                });
            }

            ImGui.PopID();

            ImGui.Separator();
        }

        private static void DrawThirdPartyImportSettings(string sourcePath, Type assetType, HashSet<object> visited)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || assetType is null)
                return;

            // Only show for the original 3rd-party file selection (not for native .asset files).
            if (string.Equals(Path.GetExtension(sourcePath), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
                return;

            var assets = Engine.Assets;
            if (assets is null)
                return;

            if (!assets.TryGetThirdPartyImportContext(sourcePath, assetType, out var importOptions, out var optionsPath, out var generatedAssetPath))
                return;

            if (!ImGui.CollapsingHeader("Import Settings", ImGuiTreeNodeFlags.DefaultOpen))
                return;

            ImGui.PushID("ThirdPartyImportSettings");

            ImGui.TextDisabled("Source");
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(sourcePath);
            ImGui.PopTextWrapPos();

            ImGui.TextDisabled("Generated Asset");
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(generatedAssetPath);
            ImGui.PopTextWrapPos();

            ImGui.TextDisabled("Options Cache");
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(optionsPath);
            ImGui.PopTextWrapPos();

            ImGui.Separator();

            DrawInspectableObject(new InspectorTargetSet(new[] { importOptions! }, importOptions!.GetType()), "ThirdPartyImportOptions", visited);

            ImGui.Spacing();
            if (ImGui.Button("Save && Reimport"))
            {
                bool saved = assets.SaveThirdPartyImportOptions(sourcePath, assetType, importOptions!);
                _ = assets.ReimportThirdPartyFileAsync(sourcePath).ContinueWith(t =>
                {
                    bool reimported = t.Status == TaskStatus.RanToCompletion && t.Result;
                    Debug.Out($"[ImportSettings] Saved={saved}, Reimported={reimported} for '{sourcePath}'.");
                });
            }

            ImGui.PopID();

            ImGui.Separator();
        }

        private static void DrawStandaloneInspectorTarget(object target)
        {
            if (target is null)
                return;

            XRAsset? assetContext = target as XRAsset;
            using var inspectorContext = new InspectorAssetContextScope(assetContext?.SourceAsset);

            string title = _inspectorStandaloneTitle ?? target.GetType().Name;
            ImGui.TextUnformatted(title);
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear##StandaloneInspectorClear"))
            {
                ClearInspectorStandaloneTarget();
                return;
            }

            string typeLabel = target.GetType().FullName ?? target.GetType().Name;
            ImGui.TextDisabled(typeLabel);

            ImGui.Separator();

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

            // If we have an associated GL API object selected, show its custom editor first
            if (_selectedOpenGlApiObject is OpenGLRenderer.GLObjectBase glObject)
            {
                if (ImGui.CollapsingHeader("OpenGL API Object", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.PushID("GLObjectInspector");
                    GLObjectEditorRegistry.DrawInspector(glObject);
                    ImGui.PopID();
                }

                ImGui.Separator();

                if (ImGui.CollapsingHeader("XR Data Properties", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.PushID("StandaloneInspector");
                    DrawStandaloneInspectorContent(target, visited);
                    ImGui.PopID();
                }
            }
            else
            {
                ImGui.PushID("StandaloneInspector");
                DrawStandaloneInspectorContent(target, visited);
                ImGui.PopID();
            }
        }

        private static partial void DrawSceneNodeBasics(SceneNode node)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSceneNodeBasics");
            const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV;
            if (!ImGui.BeginTable("SceneNodeBasics", 2, tableFlags))
                return;

            DrawInspectorRow("Name", () =>
            {
                string name = node.Name ?? string.Empty;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputText("##SceneNodeName", ref name, 256))
                {
                    string trimmed = name.Trim();
                    if (!string.Equals(trimmed, node.Name, StringComparison.Ordinal))
                        node.Name = string.IsNullOrWhiteSpace(trimmed) ? SceneNode.DefaultName : trimmed;
                }
            });

            DrawInspectorRow("Active Self", () =>
            {
                bool active = node.IsActiveSelf;
                if (ImGui.Checkbox("##SceneNodeActiveSelf", ref active))
                    node.IsActiveSelf = active;
            });

            DrawInspectorRow("Active In Hierarchy", () =>
            {
                bool active = node.IsActiveInHierarchy;
                if (ImGui.Checkbox("##SceneNodeActiveInHierarchy", ref active))
                    node.IsActiveInHierarchy = active;
            });

            DrawInspectorRow("ID", () => ImGui.TextUnformatted(node.ID.ToString()));

            DrawInspectorRow("Path", () =>
            {
                ImGui.PushTextWrapPos();
                ImGui.TextUnformatted(node.GetPath());
                ImGui.PopTextWrapPos();
            });

            ImGui.EndTable();
        }

        private static partial void DrawInspectorRow(string label, Action drawValue)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(label);
            ImGui.TableSetColumnIndex(1);
            drawValue();
        }

        private static partial void DrawTransformInspector(TransformBase transform, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawTransformInspector");
            ImGui.PushID(transform.GetHashCode());

            DrawTransformTypeToolbar(transform);
            ImGui.Spacing();

            var editor = ResolveTransformEditor(transform.GetType());
            if (editor is null)
            {
                DrawDefaultTransformInspector(transform, visited);
                ImGui.PopID();
                return;
            }

            try
            {
                editor.DrawInspector(transform, visited);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Custom transform editor '{editor.GetType().FullName}' failed for '{transform.GetType().FullName}'");
                DrawDefaultTransformInspector(transform, visited);
            }

            ImGui.PopID();
        }

        private static void DrawTransformInspector(IReadOnlyList<TransformBase> transforms, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawTransformInspector.Multi");
            if (transforms.Count == 0)
            {
                ImGui.TextDisabled("No transforms selected.");
                return;
            }

            var targetSet = CreateInspectorTargetSet(transforms.Cast<object>());
            if (targetSet.CommonType != typeof(TransformBase))
                ImGui.TextDisabled($"Transform Type: {targetSet.CommonType.Name}");
            else if (transforms.Select(t => t.GetType()).Distinct().Count() > 1)
                ImGui.TextDisabled("Transform Type: <multiple>");

            DrawInspectableObject(targetSet, "TransformPropertiesMulti", visited);
        }

        private static void DrawTransformTypeToolbar(TransformBase transform)
        {
            string popupId = $"TransformTypePopup##{transform.GetHashCode()}";
            string currentLabel = GetTransformTypeDisplayName(transform.GetType());

            ImGui.TextUnformatted($"Type: {currentLabel}");
            ImGui.SameLine();
            if (ImGui.Button("Change Type..."))
            {
                _transformTypeSearch = string.Empty;
                ImGui.OpenPopup(popupId);
            }

            DrawTransformTypePopup(transform, popupId);
        }

        private static void DrawTransformTypePopup(TransformBase transform, string popupId)
        {
            if (!ImGui.BeginPopup(popupId))
                return;

            string filter = _transformTypeSearch ?? string.Empty;
            bool hasFilter = !string.IsNullOrWhiteSpace(filter);
            string currentFullName = transform.GetType().FullName ?? transform.GetType().Name;

            ImGui.TextDisabled($"Current: {currentFullName}");
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##TransformTypeSearch", "Search transform types...", ref _transformTypeSearch, 256);
            ImGui.Spacing();

            var entries = EnsureTransformTypeEntries();
            int matchCount = 0;

            if (ImGui.BeginChild("TransformTypeList", new Vector2(0f, 260f), ImGuiChildFlags.Border))
            {
                foreach (var entry in entries)
                {
                    if (hasFilter)
                    {
                        if (!entry.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                            !entry.Tooltip.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    matchCount++;
                    bool isCurrent = entry.Type == transform.GetType();
                    var selectableFlags = isCurrent ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None;
                    if (ImGui.Selectable(entry.Label, isCurrent, selectableFlags))
                    {
                        TryChangeTransformType(transform, entry.Type);
                        ImGui.CloseCurrentPopup();
                        ImGui.EndChild();
                        ImGui.EndPopup();
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(entry.Tooltip) && ImGui.IsItemHovered(ImGuiHoveredFlags.Stationary))
                        ImGui.SetTooltip(entry.Tooltip);
                }

                if (matchCount == 0)
                    ImGui.TextDisabled("No transform types found.");

                ImGui.EndChild();
            }

            ImGui.EndPopup();
        }

        private static IReadOnlyList<TransformTypeEntry> EnsureTransformTypeEntries()
        {
            if (_transformTypeEntries is not null)
                return _transformTypeEntries;

            var entries = TransformBase.TransformTypes
                .Where(t => t is not null)
                .Where(t => !t.IsAbstract && !t.IsInterface && !t.IsGenericTypeDefinition)
                .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
                .Select(CreateTransformTypeEntry)
                .OrderBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _transformTypeEntries = entries;
            return _transformTypeEntries;
        }

        private static TransformTypeEntry CreateTransformTypeEntry(Type type)
        {
            string displayName = GetTransformTypeDisplayName(type);
            string assemblyName = type.Assembly.GetName().Name ?? type.Assembly.FullName ?? string.Empty;
            string label = string.IsNullOrWhiteSpace(assemblyName)
                ? displayName
                : $"{displayName}  ({assemblyName})";
            string tooltip = type.FullName ?? type.Name;
            return new TransformTypeEntry(type, label, tooltip);
        }

        private static string GetTransformTypeDisplayName(Type type)
        {
            var attr = type.GetCustomAttribute<DisplayNameAttribute>();
            if (attr is not null && !string.IsNullOrWhiteSpace(attr.DisplayName))
                return attr.DisplayName;
            return type.Name;
        }

        private static void TryChangeTransformType(TransformBase current, Type targetType)
        {
            if (current.GetType() == targetType)
                return;

            var node = current.SceneNode;
            if (node is null)
            {
                Debug.LogWarning($"Cannot change transform type for '{current.Name}' because it has no owning scene node.");
                return;
            }

            TransformBase? replacement;
            try
            {
                replacement = Activator.CreateInstance(targetType) as TransformBase;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to instantiate transform '{targetType.FullName}'.");
                return;
            }

            if (replacement is null)
            {
                Debug.LogWarning($"Transform type '{targetType.FullName}' could not be created because it lacks a public parameterless constructor.");
                return;
            }

            replacement.Name = current.Name;
            replacement.DeriveWorldMatrix(current.WorldMatrix);

            string nodeLabel = TransformEditorUtil.GetTransformDisplayName(current);

            try
            {
                using var interaction = Undo.BeginUserInteraction();
                using var scope = Undo.BeginChange($"Change {nodeLabel} Transform Type");
                Undo.Track(node);
                Undo.Track(current);
                node.SetTransform(replacement, TransformTypeChangeFlags);
                Undo.Track(replacement);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to assign transform '{targetType.FullName}' to node '{node.Name}'.");
            }
        }

        private readonly record struct TransformTypeEntry(Type Type, string Label, string Tooltip);

        private static partial void DrawComponentInspectors(SceneNode node, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawComponentInspectors");
            var componentsSnapshot = node.Components.ToArray();
            bool anyComponentsDrawn = false;
            List<XRComponent>? componentsToRemove = null;

            foreach (var component in componentsSnapshot)
            {
                if (component is null)
                    continue;

                anyComponentsDrawn = true;
                int componentHash = component.GetHashCode();
                ImGui.PushID(componentHash);

                string typeName = component.GetType().Name;
                string? componentName = string.IsNullOrWhiteSpace(component.Name) ? null : component.Name;
                string fullDisplayLabel = componentName is null ? typeName : $"{componentName} ({typeName})";
                string renamePopupId = $"ComponentRenamePopup##{componentHash}";

                bool renameRequested = false;

                bool open = false;
                const ImGuiTableFlags headerRowFlags = ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings;
                if (ImGui.BeginTable("ComponentHeaderRow", 4, headerRowFlags))
                {
                    ImGui.TableSetupColumn("Header", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Active", ImGuiTableColumnFlags.WidthFixed);
                    ImGui.TableSetupColumn("Rename", ImGuiTableColumnFlags.WidthFixed);
                    ImGui.TableSetupColumn("Remove", ImGuiTableColumnFlags.WidthFixed);
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    float headerAvail = ImGui.GetContentRegionAvail().X;
                    string visibleLabel = fullDisplayLabel;
                    if (componentName is not null)
                    {
                        float fullWidth = ImGui.CalcTextSize(fullDisplayLabel).X;
                        if (fullWidth > headerAvail)
                            visibleLabel = componentName;
                    }

                    string headerLabel = $"{visibleLabel}##Component{componentHash}";
                    ImGuiTreeNodeFlags headerFlags = ImGuiTreeNodeFlags.DefaultOpen;
                    open = ImGui.CollapsingHeader(headerLabel, headerFlags);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(fullDisplayLabel);

                    ImGui.TableSetColumnIndex(1);
                    using (new ImGuiDisabledScope(component.IsDestroyed))
                    {
                        bool active = component.IsActive;
                        if (ImGui.Checkbox("##ComponentActive", ref active))
                            component.IsActive = active;
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Toggle component active state");
                    }

                    ImGui.TableSetColumnIndex(2);
                    using (new ImGuiDisabledScope(component.IsDestroyed))
                    {
                        if (ImGui.SmallButton("Rename"))
                        {
                            renameRequested = true;
                        }
                    }

                    ImGui.TableSetColumnIndex(3);
                    using (new ImGuiDisabledScope(component.IsDestroyed))
                    {
                        if (ImGui.SmallButton("Remove"))
                        {
                            componentsToRemove ??= new List<XRComponent>();
                            componentsToRemove.Add(component);
                        }
                    }

                    ImGui.EndTable();
                }

                if (renameRequested)
                {
                    BeginComponentRename(component);
                    ImGui.OpenPopup(renamePopupId);
                }

                if (ReferenceEquals(_componentPendingRename, component) && ImGui.BeginPopup(renamePopupId))
                {
                    ImGui.TextUnformatted("Rename Component");
                    ImGui.Spacing();

                    if (_componentRenameInputFocusRequested)
                    {
                        ImGui.SetKeyboardFocusHere();
                        _componentRenameInputFocusRequested = false;
                    }

                    bool submitted = ImGui.InputText("##RenameComponent", _componentRenameBuffer, (uint)_componentRenameBuffer.Length,
                        ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
                    bool cancel = ImGui.IsKeyPressed(ImGuiKey.Escape);

                    ImGui.Spacing();
                    bool okClicked = ImGui.Button("OK");
                    ImGui.SameLine();
                    bool cancelClicked = ImGui.Button("Cancel");

                    if (cancel || cancelClicked)
                    {
                        CancelComponentRename();
                        ImGui.CloseCurrentPopup();
                    }
                    else if (submitted || okClicked)
                    {
                        ApplyComponentRename();
                        ImGui.CloseCurrentPopup();
                    }

                    ImGui.EndPopup();
                }

                if (open)
                {
                    var labels = GetComponentInspectorLabels(component.GetType());
                    ImGui.Indent();

                    if (!string.IsNullOrWhiteSpace(labels.Header))
                    {
                        ImGui.TextWrapped(labels.Header);
                        ImGui.Spacing();
                    }

                    DrawComponentInspector(component, visited);

                    if (!string.IsNullOrWhiteSpace(labels.Footer))
                    {
                        ImGui.Spacing();
                        ImGui.TextWrapped(labels.Footer);
                    }

                    ImGui.Unindent();
                }

                ImGui.PopID();
            }

            if (componentsToRemove is not null)
            {
                foreach (var component in componentsToRemove)
                {
                    if (component is null || component.IsDestroyed)
                        continue;

                    var componentCapture = component;
                    EnqueueSceneEdit(() =>
                    {
                        try
                        {
                            if (componentCapture is not null && !componentCapture.IsDestroyed)
                                componentCapture.Destroy();
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex, $"Failed to remove component '{componentCapture?.GetType().Name}'.");
                        }
                    });
                }
            }

            if (!anyComponentsDrawn)
                ImGui.TextDisabled("No components attached to this scene node.");
        }

        private static void DrawComponentInspectors(IReadOnlyList<SceneNode> nodes, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawComponentInspectors.Multi");
            if (nodes.Count == 0)
            {
                ImGui.TextDisabled("No components available.");
                return;
            }

            var componentsByType = new Dictionary<Type, List<XRComponent>>();
            var typeCounts = new Dictionary<Type, int>();
            var invalidTypes = new HashSet<Type>();

            foreach (var node in nodes)
            {
                var grouped = node.Components.Where(c => c is not null && !c.IsDestroyed).GroupBy(c => c!.GetType());
                foreach (var group in grouped)
                {
                    var type = group.Key;
                    typeCounts[type] = typeCounts.TryGetValue(type, out int count) ? count + 1 : 1;

                    if (group.Count() > 1)
                    {
                        invalidTypes.Add(type);
                        continue;
                    }

                    if (!componentsByType.TryGetValue(type, out var list))
                    {
                        list = new List<XRComponent>();
                        componentsByType[type] = list;
                    }

                    var component = group.First();
                    if (component is not null)
                        list.Add(component);
                }
            }

            var sharedTypes = componentsByType.Keys
                .Where(type => !invalidTypes.Contains(type)
                               && typeCounts.TryGetValue(type, out int count)
                               && count == nodes.Count
                               && componentsByType[type].Count == nodes.Count)
                .OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sharedTypes.Count == 0)
            {
                ImGui.TextDisabled("No shared components across the selection.");
                return;
            }

            foreach (var type in sharedTypes)
            {
                var components = componentsByType[type];
                DrawMultiComponentInspector(type, components, visited);
                ImGui.Spacing();
            }

            int totalTypes = typeCounts.Count;
            if (sharedTypes.Count < totalTypes || invalidTypes.Count > 0)
            {
                ImGui.TextDisabled("Some components are not shared across the selection.");
            }
        }

        private static void DrawMultiComponentInspector(Type componentType, IReadOnlyList<XRComponent> components, HashSet<object> visited)
        {
            var componentList = components.Where(c => c is not null && !c.IsDestroyed).ToList();
            if (componentList.Count == 0)
            {
                ImGui.TextDisabled($"No active components of type {componentType.Name}.");
                return;
            }

            int componentHash = componentType.GetHashCode();
            ImGui.PushID(componentHash);

            var labels = GetComponentInspectorLabels(componentType);

            string? commonName = componentList.Select(c => c.Name ?? string.Empty).Distinct().Count() == 1
                ? componentList[0].Name
                : null;
            string displayLabel = string.IsNullOrWhiteSpace(commonName) ? componentType.Name : $"{commonName} ({componentType.Name})";
            string headerLabel = $"{displayLabel}##ComponentMulti{componentHash}";

            bool removeRequested = false;
            bool open;
            const ImGuiTableFlags headerRowFlags = ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings;
            if (ImGui.BeginTable("ComponentHeaderRowMulti", 3, headerRowFlags))
            {
                ImGui.TableSetupColumn("Header", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Active", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Remove", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                open = ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.DefaultOpen);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{componentType.FullName} ({componentList.Count} selected)");

                ImGui.TableSetColumnIndex(1);
                bool allActive = componentList.All(c => c.IsActive);
                bool allInactive = componentList.All(c => !c.IsActive);
                bool mixed = !allActive && !allInactive;
                bool active = allActive;
                if (mixed)
                    ImGui.PushItemFlag(ImGuiItemFlags.MixedValue, true);
                bool toggled = ImGui.Checkbox("##ComponentActiveMulti", ref active);
                if (mixed)
                    ImGui.PopItemFlag();
                if (toggled)
                {
                    foreach (var component in componentList)
                        component.IsActive = active;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Toggle component active state");

                ImGui.TableSetColumnIndex(2);
                if (ImGui.SmallButton("Remove"))
                    removeRequested = true;

                ImGui.EndTable();
            }
            else
            {
                open = ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.DefaultOpen);
            }

            if (removeRequested)
            {
                foreach (var component in componentList)
                {
                    var componentCapture = component;
                    EnqueueSceneEdit(() =>
                    {
                        try
                        {
                            if (!componentCapture.IsDestroyed)
                                componentCapture.Destroy();
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex, $"Failed to remove component '{componentCapture?.GetType().Name}'.");
                        }
                    });
                }
            }

            if (open)
            {
                ImGui.Indent();

                if (!string.IsNullOrWhiteSpace(labels.Header))
                {
                    ImGui.TextWrapped(labels.Header);
                    ImGui.Spacing();
                }

                var targetSet = new InspectorTargetSet(componentList.Cast<object>().ToList(), componentType);
                DrawInspectableObject(targetSet, $"ComponentPropertiesMulti_{componentHash}", visited);

                if (!string.IsNullOrWhiteSpace(labels.Footer))
                {
                    ImGui.Spacing();
                    ImGui.TextWrapped(labels.Footer);
                }

                ImGui.Unindent();
            }

            ImGui.PopID();
        }

        private static partial void DrawInspectableObject(InspectorTargetSet targets, string id, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawInspectableObject");
            object primary = targets.PrimaryTarget;
            if (!visited.Add(primary))
            {
                ImGui.TextUnformatted("<circular reference>");
                return;
            }

            ImGui.PushID(id);
            DrawSettingsProperties(targets, visited);
            ImGui.PopID();

            visited.Remove(primary);
        }

        private static partial void DrawComponentInspector(XRComponent component, HashSet<object> visited)
        {
            // Add possess button for all PawnComponent types
            if (component is PawnComponent pawnComponent)
            {
                DrawPawnPossessButton(pawnComponent);
            }

            var editor = ResolveComponentEditor(component.GetType());
            if (editor is null)
            {
                DrawDefaultComponentInspector(component, visited);
                return;
            }

            try
            {
                editor.DrawInspector(component, visited);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Custom component editor '{editor.GetType().FullName}' failed for '{component.GetType().FullName}'");
                DrawDefaultComponentInspector(component, visited);
            }
        }

        private static void DrawPawnPossessButton(PawnComponent pawn)
        {
            var mainPlayer = Engine.State.MainPlayer;
            bool isCurrentlyPossessed = ReferenceEquals(mainPlayer?.ControlledPawn, pawn);

            if (isCurrentlyPossessed)
            {
                ImGui.TextColored(new Vector4(0.4f, 0.8f, 0.4f, 1.0f), "✓ Currently Possessed by Player 1");
            }
            else
            {
                if (ImGui.Button("Possess as Player 1"))
                {
                    PossessPawnAsMainPlayer(pawn);
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Make Player 1 possess this pawn. This will switch the main viewport camera to this pawn's camera.");
                }
            }
            ImGui.Spacing();
        }

        private static void PossessPawnAsMainPlayer(PawnComponent pawn)
        {
            var mainPlayer = Engine.State.MainPlayer;
            if (mainPlayer is null)
            {
                Debug.LogWarning("Cannot possess pawn: no main player exists.");
                return;
            }

            // Unlink from current pawn first
            mainPlayer.UnlinkControlledPawn();

            // Ensure player has a valid viewport
            var window = Engine.Windows.FirstOrDefault();
            if (window is not null)
            {
                var ensuredViewport = window.EnsureControllerRegistered(mainPlayer, autoSizeAllViewports: false);
                if (ensuredViewport is not null)
                {
                    mainPlayer.Input.UpdateDevices(ensuredViewport.Window?.Input, Engine.VRState.Actions);
                }
            }

            // Set the controlled pawn - this triggers UpdateViewportCamera
            mainPlayer.ControlledPawn = pawn;

            // Force refresh the viewport camera binding
            mainPlayer.RefreshViewportCamera();

            Debug.Out($"[EditorImGuiUI] Possessed pawn '{pawn.Name ?? pawn.GetType().Name}' as Player 1");
        }

        public static partial void DrawDefaultComponentInspector(XRComponent component, HashSet<object> visited)
            => DrawInspectableObject(new InspectorTargetSet(new[] { component }, component.GetType()), "ComponentProperties", visited);

        public static partial void DrawDefaultTransformInspector(TransformBase transform, HashSet<object> visited)
            => DrawInspectableObject(new InspectorTargetSet(new[] { transform }, transform.GetType()), "TransformProperties", visited);

        public static void DrawDefaultAssetInspector(XRAsset asset, HashSet<object> visited)
            => DrawInspectableObject(new InspectorTargetSet(new[] { asset }, asset.GetType()), "AssetProperties", visited);

        public static void DrawDefaultAssetInspector(InspectorTargetSet targets, HashSet<object> visited)
            => DrawInspectableObject(targets, "AssetProperties", visited);

        internal static void DrawAssetInspectorInline(XRAsset asset)
        {
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var targets = new InspectorTargetSet(new[] { asset }, asset.GetType());
            if (!TryDrawAssetInspector(targets, visited))
                DrawDefaultAssetInspector(asset, visited);
        }

        private static bool TryDrawAssetInspector(InspectorTargetSet targets, HashSet<object> visited)
        {
            var inspector = ResolveAssetInspector(targets.CommonType);
            if (inspector is null)
                return false;

            try
            {
                inspector.DrawInspector(targets, visited);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Custom asset inspector '{inspector.GetType().FullName}' failed for '{targets.CommonType.FullName}'");
                foreach (var obj in targets.Targets)
                {
                    if (obj is XRAsset asset)
                        DrawDefaultAssetInspector(asset, visited);
                }
                return true;
            }
        }

        private static IXRAssetInspector? ResolveAssetInspector(Type assetType)
        {
            if (_assetInspectorCache.TryGetValue(assetType, out var cached))
                return cached;

            var attribute = assetType.GetCustomAttribute<XRAssetInspectorAttribute>(true);
            if (attribute is null || string.IsNullOrWhiteSpace(attribute.InspectorTypeName))
            {
                _assetInspectorCache[assetType] = null;
                return null;
            }

            Type? inspectorType = null;
            string typeName = attribute.InspectorTypeName;

            inspectorType = Type.GetType(typeName, false, false);
            if (inspectorType is null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    inspectorType = assembly.GetType(typeName, false, false);
                    if (inspectorType is not null)
                        break;
                }
            }

            if (inspectorType is null)
            {
                Debug.LogWarning($"Failed to locate asset inspector '{typeName}' for asset '{assetType.FullName}'.");
                _assetInspectorCache[assetType] = null;
                return null;
            }

            if (!typeof(IXRAssetInspector).IsAssignableFrom(inspectorType))
            {
                Debug.LogWarning($"Asset inspector type '{inspectorType.FullName}' does not implement {nameof(IXRAssetInspector)}.");
                _assetInspectorCache[assetType] = null;
                return null;
            }

            try
            {
                if (Activator.CreateInstance(inspectorType) is IXRAssetInspector inspectorInstance)
                {
                    _assetInspectorCache[assetType] = inspectorInstance;
                    return inspectorInstance;
                }

                Debug.LogWarning($"Asset inspector '{inspectorType.FullName}' could not be instantiated.");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to instantiate asset inspector '{inspectorType.FullName}' for asset '{assetType.FullName}'");
            }

            _assetInspectorCache[assetType] = null;
            return null;
        }

        private static partial IXRComponentEditor? ResolveComponentEditor(Type componentType)
        {
            if (_componentEditorCache.TryGetValue(componentType, out var cached))
                return cached;

            var attribute = componentType.GetCustomAttribute<XRComponentEditorAttribute>(true);
            if (attribute is null)
            {
                _componentEditorCache[componentType] = null;
                return null;
            }

            Type? editorType = null;
            string typeName = attribute.EditorTypeName;

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                editorType = Type.GetType(typeName, false, false);
                if (editorType is null)
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        editorType = assembly.GetType(typeName, false, false);
                        if (editorType is not null)
                            break;
                    }
                }
            }

            if (editorType is null)
            {
                Debug.LogWarning($"Failed to locate component editor '{typeName}' for component '{componentType.FullName}'.");
                _componentEditorCache[componentType] = null;
                return null;
            }

            if (!typeof(IXRComponentEditor).IsAssignableFrom(editorType))
            {
                Debug.LogWarning($"Component editor type '{editorType.FullName}' does not implement {nameof(IXRComponentEditor)}.");
                _componentEditorCache[componentType] = null;
                return null;
            }

            try
            {
                if (Activator.CreateInstance(editorType) is IXRComponentEditor editorInstance)
                {
                    _componentEditorCache[componentType] = editorInstance;
                    return editorInstance;
                }

                Debug.LogWarning($"Component editor '{editorType.FullName}' could not be instantiated.");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to instantiate component editor '{editorType.FullName}' for component '{componentType.FullName}'");
            }

            _componentEditorCache[componentType] = null;
            return null;
        }

        private static partial IXRTransformEditor? ResolveTransformEditor(Type transformType)
        {
            if (_transformEditorCache.TryGetValue(transformType, out var cached))
                return cached;

            var attribute = transformType.GetCustomAttribute<XRTransformEditorAttribute>(true);
            if (attribute is null)
            {
                _transformEditorCache[transformType] = null;
                return null;
            }

            Type? editorType = null;
            string typeName = attribute.EditorTypeName;

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                editorType = Type.GetType(typeName, false, false);
                if (editorType is null)
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        editorType = assembly.GetType(typeName, false, false);
                        if (editorType is not null)
                            break;
                    }
                }
            }

            if (editorType is null)
            {
                Debug.LogWarning($"Failed to locate transform editor '{typeName}' for transform '{transformType.FullName}'.");
                _transformEditorCache[transformType] = null;
                return null;
            }

            if (!typeof(IXRTransformEditor).IsAssignableFrom(editorType))
            {
                Debug.LogWarning($"Transform editor type '{editorType.FullName}' does not implement {nameof(IXRTransformEditor)}.");
                _transformEditorCache[transformType] = null;
                return null;
            }

            try
            {
                if (Activator.CreateInstance(editorType) is IXRTransformEditor editorInstance)
                {
                    _transformEditorCache[transformType] = editorInstance;
                    return editorInstance;
                }

                Debug.LogWarning($"Transform editor '{editorType.FullName}' could not be instantiated.");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to instantiate transform editor '{editorType.FullName}' for transform '{transformType.FullName}'");
            }

            _transformEditorCache[transformType] = null;
            return null;
        }


        private static partial void InvalidateComponentTypeCache()
            => _componentTypeCacheDirty = true;

        private static partial IReadOnlyList<ComponentTypeDescriptor> EnsureComponentTypeCache()
        {
            if (!_componentTypeCacheDirty && _componentTypeDescriptors.Count > 0)
                return _componentTypeDescriptors;

            _componentTypeDescriptors.Clear();

            var baseType = typeof(XRComponent);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
                }

                foreach (var type in types)
                {
                    if (type is null)
                        continue;
                    if (!baseType.IsAssignableFrom(type))
                        continue;
                    if (type.IsAbstract || type.IsInterface)
                        continue;
                    if (type.ContainsGenericParameters)
                        continue;

                    string displayName = type.Name;
                    string ns = type.Namespace ?? string.Empty;
                    string assemblyName = assembly.GetName().Name ?? assembly.FullName ?? "Unknown";

                    _componentTypeDescriptors.Add(new ComponentTypeDescriptor(type, displayName, ns, assemblyName));
                }
            }

            _componentTypeDescriptors.Sort(static (a, b) =>
            {
                int nameCompare = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                if (nameCompare != 0)
                    return nameCompare;
                int nsCompare = string.Compare(a.Namespace, b.Namespace, StringComparison.Ordinal);
                if (nsCompare != 0)
                    return nsCompare;
                return string.Compare(a.AssemblyName, b.AssemblyName, StringComparison.OrdinalIgnoreCase);
            });

            _componentTypeCacheDirty = false;
            return _componentTypeDescriptors;
        }

        private static partial IReadOnlyList<ComponentTypeDescriptor> GetFilteredComponentTypes(string? search)
        {
            var all = EnsureComponentTypeCache();
            _filteredComponentTypes.Clear();

            if (string.IsNullOrWhiteSpace(search))
            {
                _filteredComponentTypes.AddRange(all);
                return _filteredComponentTypes;
            }

            string term = search.Trim();
            foreach (var descriptor in all)
            {
                if (descriptor.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(descriptor.Namespace) && descriptor.Namespace.Contains(term, StringComparison.OrdinalIgnoreCase))
                    || descriptor.AssemblyName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || descriptor.FullName.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    _filteredComponentTypes.Add(descriptor);
                }
            }

            return _filteredComponentTypes;
        }

        private static partial void CloseComponentPickerPopup()
        {
            ResetComponentPickerState();
            _addComponentPopupOpen = false;
            ImGui.CloseCurrentPopup();
        }

    private static partial void ResetComponentPickerState()
    {
        _nodesPendingAddComponent = null;
        _componentPickerError = null;
        _componentPickerSearch = string.Empty;
        _addComponentPopupRequested = false;
    }
}
