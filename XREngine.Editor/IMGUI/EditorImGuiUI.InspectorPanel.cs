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
using XREngine.Input.Devices;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static string _inspectorPropertySearch = string.Empty;

        private static XRComponent? _componentPendingRename;
        private static bool _componentRenameInputFocusRequested;
        private static readonly byte[] _componentRenameBuffer = new byte[256];

        /// <summary>Scratch list reused each frame to avoid ToArray() on Components EventList.</summary>
        private static readonly List<XRComponent> _componentsScratch = [];
        /// <summary>Reusable visited set for inspector draws, cleared each frame.</summary>
        private static readonly HashSet<object> _visitedScratch = new(ReferenceEqualityComparer.Instance);
        /// <summary>Cached component header labels keyed by (componentHashCode, componentName, typeName).</summary>
        private static readonly Dictionary<(int Hash, string? Name, string TypeName), (string FullDisplay, string Header, string RenamePopup)> _componentLabelCache = [];

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
            using var _ = Undo.TrackChange("Rename Component", _componentPendingRename);
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
                    var addedComponents = new List<(SceneNode node, XRComponent comp)>();

                    foreach (var node in targetNodes)
                    {
                        if (!node.TryAddComponent(requestedComponent, out var comp) || comp is null)
                            failedNodes.Add(node);
                        else
                            addedComponents.Add((node, comp));
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

                    // Record undo for successfully added components
                    if (addedComponents.Count > 0)
                    {
                        string compName = requestedComponent.Name;
                        // Capture for closure
                        var capturedPairs = addedComponents.ToArray();

                        using var interaction = Undo.BeginUserInteraction();
                        using var scope = Undo.BeginChange($"Add {compName}");
                        foreach (var (n, c) in capturedPairs)
                            Undo.Track(c);

                        Undo.RecordStructuralChange($"Add {compName}",
                            undoAction: () =>
                            {
                                foreach (var (n, c) in capturedPairs)
                                    n.DetachComponent(c);
                            },
                            redoAction: () =>
                            {
                                foreach (var (n, c) in capturedPairs)
                                    n.ReattachComponent(c);
                            });
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
            ImGuiTextFieldHelper.DrawTextFieldContextMenu("ctx_InspSearch", ref _inspectorPropertySearch);
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

            ImGui.PushID("SceneNodeMultiInspector");

            ImGui.TextUnformatted($"{nodes.Count} Scene Nodes Selected");
            ImGui.TextDisabled("Editing shared values will apply to all selected nodes.");
            ImGui.Separator();

            DrawSceneNodeBasics(nodes);

            ImGui.Separator();

            if (nodes.Count > MaxDetailedMultiSelectionInspectorCount)
            {
                ImGui.TextDisabled($"Detailed transform and component inspectors are skipped for selections larger than {MaxDetailedMultiSelectionInspectorCount} nodes.");
                ImGui.TextDisabled("Use the hierarchy filter or select fewer nodes for per-property editing.");

                ImGui.Spacing();
                if (ImGui.Button("Add Component to Selected..."))
                    BeginAddComponentForHierarchyNodes(nodes);

                ImGui.PopID();
                return;
            }

            _visitedScratch.Clear();
            foreach (var node in nodes)
                _visitedScratch.Add(node);

            if (ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.PushID("TransformSection");
                DrawTransformInspector(nodes, _visitedScratch);
                ImGui.PopID();
            }

            ImGui.Separator();
            ImGui.TextUnformatted("Components");
            ImGui.SameLine();
            if (ImGui.Button("Add Component to Selected..."))
                BeginAddComponentForHierarchyNodes(nodes);

            ImGui.Spacing();
            DrawComponentInspectors(nodes, _visitedScratch);

            ImGui.PopID();
        }

        private const int MaxDetailedMultiSelectionInspectorCount = 64;
        private static readonly List<TransformBase> _multiTransformScratch = [];
        private static readonly List<object> _multiTransformTargetsScratch = [];

        private static partial void DrawSceneNodeInspector(SceneNode node)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSceneNodeInspector");
            _visitedScratch.Clear();
            _visitedScratch.Add(node);

            ImGui.PushID(node.ID.ToString());

            DrawSceneNodeBasics(node);

            ImGui.Separator();

            if (ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.PushID("TransformSection");
                DrawTransformInspector(node.Transform, _visitedScratch);
                ImGui.PopID();
            }

            ImGui.Separator();
            ImGui.TextUnformatted("Components");
            ImGui.SameLine();
            if (ImGui.Button("Add Component..."))
                BeginAddComponentForHierarchyNode(node);

            ImGui.Spacing();
            DrawComponentInspectors(node, _visitedScratch);

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
                string firstName = nodes[0].Name ?? string.Empty;
                bool sameName = true;
                for (int i = 1; i < nodes.Count; i++)
                {
                    if (!string.Equals(nodes[i].Name ?? string.Empty, firstName, StringComparison.Ordinal))
                    {
                        sameName = false;
                        break;
                    }
                }

                string? commonName = sameName ? firstName : null;
                string name = sameName ? firstName : string.Empty;
                ImGui.SetNextItemWidth(-1f);
                string hint = commonName is null ? "<multiple>" : string.Empty;
                if (ImGui.InputTextWithHint("##SceneNodeNameMulti", hint, ref name, 256))
                {
                    string trimmed = name.Trim();
                    string newName = string.IsNullOrWhiteSpace(trimmed) ? SceneNode.DefaultName : trimmed;
                    foreach (var node in nodes)
                    {
                        ImGuiUndoHelper.TrackDragUndo("Rename Node", node);
                        node.Name = newName;
                    }
                }
                if (ImGuiTextFieldHelper.DrawTextFieldContextMenu("ctx_NodeNameMulti", ref name))
                {
                    string trimmed = name.Trim();
                    string newName = string.IsNullOrWhiteSpace(trimmed) ? SceneNode.DefaultName : trimmed;
                    foreach (var node in nodes)
                    {
                        ImGuiUndoHelper.TrackDragUndo("Rename Node", node);
                        node.Name = newName;
                    }
                }
            });

            DrawInspectorRow("Active Self", () =>
            {
                bool allActive = true;
                bool allInactive = true;
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].IsActiveSelf)
                        allInactive = false;
                    else
                        allActive = false;
                }
                bool mixed = !allActive && !allInactive;
                bool active = allActive;
                if (mixed)
                    ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.6f);
                bool toggled = ImGui.Checkbox("##SceneNodeActiveSelfMulti", ref active);
                if (mixed)
                    ImGui.PopStyleVar();
                if (toggled)
                {
                    using var interaction = Undo.BeginUserInteraction();
                    using var scope = Undo.BeginChange("Toggle Active Self");
                    foreach (var node in nodes)
                    {
                        Undo.Track(node);
                        node.IsActiveSelf = active;
                    }
                }
            });

            DrawInspectorRow("Active In Hierarchy", () =>
            {
                bool allActive = true;
                bool allInactive = true;
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].IsActiveInHierarchy)
                        allInactive = false;
                    else
                        allActive = false;
                }
                bool mixed = !allActive && !allInactive;
                bool active = allActive;
                if (mixed)
                    ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.6f);
                bool toggled = ImGui.Checkbox("##SceneNodeActiveInHierarchyMulti", ref active);
                if (mixed)
                    ImGui.PopStyleVar();
                if (toggled)
                {
                    using var interaction = Undo.BeginUserInteraction();
                    using var scope = Undo.BeginChange("Toggle Active In Hierarchy");
                    foreach (var node in nodes)
                    {
                        Undo.Track(node);
                        node.IsActiveInHierarchy = active;
                    }
                }
            });

            DrawInspectorRow("ID", () =>
            {
                var firstId = nodes[0].ID;
                bool same = true;
                for (int i = 1; i < nodes.Count; i++)
                {
                    if (!Equals(nodes[i].ID, firstId))
                    {
                        same = false;
                        break;
                    }
                }
                ImGui.TextUnformatted(same ? nodes[0].ID.ToString() : "<multiple>");
            });

            DrawInspectorRow("Path", () =>
            {
                string firstPath = nodes[0].GetPath();
                bool same = true;
                for (int i = 1; i < nodes.Count; i++)
                {
                    if (!string.Equals(nodes[i].GetPath(), firstPath, StringComparison.Ordinal))
                    {
                        same = false;
                        break;
                    }
                }
                ImGui.PushTextWrapPos();
                ImGui.TextUnformatted(same ? firstPath : "<multiple>");
                ImGui.PopTextWrapPos();
            });

            ImGui.EndTable();
        }

        private static void DrawStandaloneInspectorContent(object target, HashSet<object> visited)
        {
            if (target is AssetExplorerInspectorLoadState loadState)
            {
                DrawAssetExplorerInspectorLoadState(loadState, visited);
                return;
            }

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
            if (assetContext is null && target is AssetExplorerInspectorLoadState loadState)
                assetContext = loadState.PartialPrefab;
            using var inspectorContext = new InspectorAssetContextScope(assetContext?.SourceAsset);

            string title = _inspectorStandaloneTitle ?? target.GetType().Name;
            ImGui.TextUnformatted(title);
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear##StandaloneInspectorClear"))
            {
                ClearInspectorStandaloneTarget();
                return;
            }

            string typeLabel = target is AssetExplorerInspectorLoadState inspectorLoadState
                ? inspectorLoadState.Descriptor.FullName
                : target.GetType().FullName ?? target.GetType().Name;
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

        private static void DrawAssetExplorerInspectorLoadState(AssetExplorerInspectorLoadState loadState, HashSet<object> visited)
        {
            if (loadState.PartialPrefab is not null)
            {
                DrawAssetExplorerLoadProgress(loadState);
                ImGui.Spacing();

                using var _ = XRPrefabSourceInspector.EnterReadOnlyScope();
                DrawDefaultAssetInspector(loadState.PartialPrefab, visited);

                if (string.IsNullOrWhiteSpace(loadState.ErrorMessage))
                    return;

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.9f, 0.25f, 0.25f, 1f), "Failed to fully hydrate prefab references.");
                ImGui.PushTextWrapPos();
                ImGui.TextUnformatted(loadState.ErrorMessage);
                ImGui.PopTextWrapPos();
                return;
            }

            ImGui.TextDisabled("Path");
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(loadState.Path);
            ImGui.PopTextWrapPos();

            ImGui.Spacing();
            DrawAssetExplorerLoadProgress(loadState);

            if (loadState.PrefabPreview is not null)
            {
                ImGui.Spacing();
                ImGui.PushID(loadState.Path);
                DrawPrefabHierarchyLoadingInspector(loadState);
                ImGui.PopID();
                ImGui.Spacing();
                ImGui.TextDisabled("Hierarchy is ready. Scene node details will switch to the live prefab inspector when the background load completes.");
                ImGui.Spacing();
            }

            if (string.IsNullOrWhiteSpace(loadState.ErrorMessage))
                return;

            ImGui.TextColored(new Vector4(0.9f, 0.25f, 0.25f, 1f), "Failed to load asset.");
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(loadState.ErrorMessage);
            ImGui.PopTextWrapPos();
        }

        private static void DrawAssetExplorerLoadProgress(AssetExplorerInspectorLoadState loadState)
        {
            ImGui.TextDisabled("Status");
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(loadState.StatusMessage);
            ImGui.PopTextWrapPos();

            string overlay = BuildAssetExplorerLoadProgressOverlay(loadState);
            ImGui.ProgressBar(Math.Clamp(loadState.ProgressFraction, 0.0f, 1.0f), new Vector2(-1f, 0f), overlay);

            if (loadState.TotalDependencyLoads > 0)
                ImGui.TextDisabled($"Referenced assets: {loadState.CompletedDependencyLoads}/{loadState.TotalDependencyLoads}");
        }

        private static string BuildAssetExplorerLoadProgressOverlay(AssetExplorerInspectorLoadState loadState)
        {
            int percent = (int)Math.Round(Math.Clamp(loadState.ProgressFraction, 0.0f, 1.0f) * 100.0f);
            return loadState.ProgressStage == AssetLoadProgressStage.ResolvingDependencies && loadState.TotalDependencyLoads > 0
                ? $"{percent}%  {loadState.CompletedDependencyLoads}/{loadState.TotalDependencyLoads} refs"
                : $"{percent}%";
        }

        private static void DrawPrefabHierarchyLoadingInspector(AssetExplorerInspectorLoadState loadState)
        {
            if (loadState.PrefabPreview is null || loadState.PrefabPreviewState is null)
                return;

            EnsureSelectedPrefabPreviewNode(loadState);

            PrefabHierarchyPreview preview = loadState.PrefabPreview;
            PrefabHierarchyPreviewState state = loadState.PrefabPreviewState;
            PrefabHierarchyPreviewNode selectedNode = FindSelectedPrefabPreviewNode(preview, state) ?? preview.RootNode;

            DrawPrefabPreviewSummary(loadState, preview, state, selectedNode);
            ImGui.Separator();

            Vector2 available = ImGui.GetContentRegionAvail();
            float spacing = ImGui.GetStyle().ItemSpacing.Y;
            const float minTreeHeight = 180.0f;
            const float minInspectorHeight = 220.0f;

            float treeHeight;
            if (available.Y > minTreeHeight + minInspectorHeight + spacing)
            {
                treeHeight = Math.Clamp(available.Y * 0.42f, minTreeHeight, available.Y - minInspectorHeight - spacing);
            }
            else
            {
                treeHeight = MathF.Max(120.0f, available.Y * 0.35f);
            }

            ImGui.SetNextItemWidth(-1.0f);
            ImGui.InputTextWithHint("##PrefabHierarchySearch", "Search prefab nodes...", ref state.SearchText, 256);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Filter nodes by name or hierarchy path.");

            bool searchActive = !string.IsNullOrWhiteSpace(state.SearchText);
            int visibleCount = CountVisiblePrefabPreviewNodes(preview.RootNode, state.SearchText);
            string hierarchyLabel = searchActive
                ? $"Hierarchy ({visibleCount}/{preview.NodeCount} visible)"
                : $"Hierarchy ({preview.NodeCount} nodes)";
            ImGui.SeparatorText(hierarchyLabel);
            if (ImGui.BeginChild("PrefabHierarchyTree", new Vector2(-1.0f, treeHeight), ImGuiChildFlags.Border))
                DrawPrefabHierarchyLoadingNode(preview.RootNode, state, depth: 0);
            ImGui.EndChild();

            ImGui.SeparatorText("Node Inspector");
            if (ImGui.BeginChild("PrefabHierarchyInspector", Vector2.Zero, ImGuiChildFlags.Border))
                DrawPrefabPreviewNodeInspector(loadState, selectedNode);
            ImGui.EndChild();
        }

        private static void DrawPrefabPreviewSummary(AssetExplorerInspectorLoadState loadState, PrefabHierarchyPreview preview, PrefabHierarchyPreviewState state, PrefabHierarchyPreviewNode selectedNode)
        {
            if (!ImGui.BeginTable("PrefabSourceSummary", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
                return;

            DrawPrefabPreviewSummaryRow("Root Node", preview.RootNode.DisplayName);
            DrawPrefabPreviewSummaryRow("Total Nodes", preview.NodeCount.ToString());
            if (!string.IsNullOrWhiteSpace(state.SearchText))
                DrawPrefabPreviewSummaryRow("Visible Nodes", CountVisiblePrefabPreviewNodes(preview.RootNode, state.SearchText).ToString());
            DrawPrefabPreviewSummaryRow("Selected Node", selectedNode.DisplayName);
            DrawPrefabPreviewSummaryRow("Load Stage", GetAssetLoadStageLabel(loadState));
            if (loadState.TotalDependencyLoads > 0)
                DrawPrefabPreviewSummaryRow("Referenced Assets", $"{loadState.CompletedDependencyLoads}/{loadState.TotalDependencyLoads}");
            ImGui.EndTable();
        }

        private static string GetAssetLoadStageLabel(AssetExplorerInspectorLoadState loadState)
            => loadState.ProgressStage switch
            {
                AssetLoadProgressStage.CheckingCache => "Checking Cache",
                AssetLoadProgressStage.OpeningFile => "Opening File",
                AssetLoadProgressStage.ParsingAssetGraph => "Parsing Prefab Graph",
                AssetLoadProgressStage.ResolvingDependencies => "Resolving Referenced Assets",
                AssetLoadProgressStage.ImportingThirdParty => "Importing Asset",
                AssetLoadProgressStage.Finalizing => "Finalizing Asset Graph",
                AssetLoadProgressStage.Completed => "Ready",
                AssetLoadProgressStage.Failed => "Failed",
                _ => "Loading"
            };

        private static void DrawPrefabPreviewSummaryRow(string label, string value)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(label);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(value);
        }

        private static void DrawPrefabHierarchyLoadingNode(PrefabHierarchyPreviewNode node, PrefabHierarchyPreviewState state, int depth)
        {
            if (!ShouldDrawPrefabPreviewNode(node, state.SearchText))
                return;

            ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
            if (depth == 0)
                flags |= ImGuiTreeNodeFlags.DefaultOpen;
            if (node.Children.Count == 0)
                flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.Bullet | ImGuiTreeNodeFlags.NoTreePushOnOpen;
            if (IsSelectedPrefabPreviewNode(node, state))
                flags |= ImGuiTreeNodeFlags.Selected;

            string label = node.Children.Count > 0
                ? $"{node.DisplayName} ({node.Children.Count})"
                : node.DisplayName;

            if (!string.IsNullOrWhiteSpace(state.SearchText) && PrefabPreviewNodeOrDescendantMatches(node, state.SearchText))
                ImGui.SetNextItemOpen(true, ImGuiCond.Once);

            ImGui.PushID(node.NodeId == Guid.Empty ? node.Path : node.NodeId.ToString());
            bool open = ImGui.TreeNodeEx("##PrefabHierarchyPreviewNode", flags, label);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                SelectPrefabPreviewNode(state, node);

            if (node.Children.Count > 0 && open)
            {
                for (int i = 0; i < node.Children.Count; i++)
                    DrawPrefabHierarchyLoadingNode(node.Children[i], state, depth + 1);

                ImGui.TreePop();
            }
            ImGui.PopID();
        }

        private static void DrawPrefabPreviewNodeInspector(AssetExplorerInspectorLoadState loadState, PrefabHierarchyPreviewNode node)
        {
            ImGui.TextUnformatted(node.DisplayName);
            ImGui.TextDisabled(node.Path);
            ImGui.Spacing();

            if (!ImGui.BeginTable("PrefabPreviewNodeInspector", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
                return;

            DrawPrefabPreviewSummaryRow("Name", node.DisplayName);
            DrawPrefabPreviewSummaryRow("Hierarchy Path", node.Path);
            DrawPrefabPreviewSummaryRow("Child Nodes", node.Children.Count.ToString());
            DrawPrefabPreviewSummaryRow("Node Status", loadState.ProgressStage >= AssetLoadProgressStage.ResolvingDependencies
                ? "Hierarchy loaded; referenced assets are still resolving."
                : "Hierarchy parsed from the prefab file.");
            ImGui.EndTable();

            ImGui.Spacing();
            ImGui.TextDisabled("Live components, transforms, and overrides will appear here once the full prefab asset finishes loading.");
        }

        private static void EnsureSelectedPrefabPreviewNode(AssetExplorerInspectorLoadState loadState)
        {
            if (loadState.PrefabPreview is null || loadState.PrefabPreviewState is null)
                return;

            if (FindSelectedPrefabPreviewNode(loadState.PrefabPreview, loadState.PrefabPreviewState) is not null)
                return;

            SelectPrefabPreviewNode(loadState.PrefabPreviewState, loadState.PrefabPreview.RootNode);
        }

        private static PrefabHierarchyPreviewNode? FindSelectedPrefabPreviewNode(PrefabHierarchyPreview preview, PrefabHierarchyPreviewState state)
            => FindSelectedPrefabPreviewNode(preview.RootNode, state);

        private static PrefabHierarchyPreviewNode? FindSelectedPrefabPreviewNode(PrefabHierarchyPreviewNode node, PrefabHierarchyPreviewState state)
        {
            if (IsSelectedPrefabPreviewNode(node, state))
                return node;

            for (int i = 0; i < node.Children.Count; i++)
            {
                PrefabHierarchyPreviewNode? found = FindSelectedPrefabPreviewNode(node.Children[i], state);
                if (found is not null)
                    return found;
            }

            return null;
        }

        private static bool IsSelectedPrefabPreviewNode(PrefabHierarchyPreviewNode node, PrefabHierarchyPreviewState state)
        {
            if (node.NodeId != Guid.Empty && state.SelectedNodeId != Guid.Empty)
                return node.NodeId == state.SelectedNodeId;

            return string.Equals(node.Path, state.SelectedNodePath, StringComparison.Ordinal);
        }

        private static void SelectPrefabPreviewNode(PrefabHierarchyPreviewState state, PrefabHierarchyPreviewNode node)
        {
            state.SelectedNodeId = node.NodeId;
            state.SelectedNodePath = node.Path;
        }

        private static int CountVisiblePrefabPreviewNodes(PrefabHierarchyPreviewNode root, string? searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return CountPrefabPreviewNodes(root);

            int count = 0;
            CountVisiblePrefabPreviewNodes(root, searchText, ref count);
            return count;
        }

        private static void CountVisiblePrefabPreviewNodes(PrefabHierarchyPreviewNode node, string searchText, ref int count)
        {
            if (ShouldDrawPrefabPreviewNode(node, searchText))
                count++;

            for (int i = 0; i < node.Children.Count; i++)
                CountVisiblePrefabPreviewNodes(node.Children[i], searchText, ref count);
        }

        private static int CountPrefabPreviewNodes(PrefabHierarchyPreviewNode node)
        {
            int count = 1;
            for (int i = 0; i < node.Children.Count; i++)
                count += CountPrefabPreviewNodes(node.Children[i]);
            return count;
        }

        private static bool ShouldDrawPrefabPreviewNode(PrefabHierarchyPreviewNode node, string? searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return true;

            return PrefabPreviewNodeMatchesSearch(node, searchText) || PrefabPreviewNodeHasMatchingDescendant(node, searchText);
        }

        private static bool PrefabPreviewNodeHasMatchingDescendant(PrefabHierarchyPreviewNode node, string searchText)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                PrefabHierarchyPreviewNode child = node.Children[i];
                if (PrefabPreviewNodeMatchesSearch(child, searchText) || PrefabPreviewNodeHasMatchingDescendant(child, searchText))
                    return true;
            }

            return false;
        }

        private static bool PrefabPreviewNodeOrDescendantMatches(PrefabHierarchyPreviewNode node, string searchText)
            => PrefabPreviewNodeMatchesSearch(node, searchText) || PrefabPreviewNodeHasMatchingDescendant(node, searchText);

        private static bool PrefabPreviewNodeMatchesSearch(PrefabHierarchyPreviewNode node, string searchText)
        {
            string trimmed = searchText.Trim();
            if (trimmed.Length == 0)
                return true;

            return node.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                || node.Path.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
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
                    {
                        ImGuiUndoHelper.TrackDragUndo("Rename Node", node);
                        node.Name = string.IsNullOrWhiteSpace(trimmed) ? SceneNode.DefaultName : trimmed;
                    }
                }
                if (ImGuiTextFieldHelper.DrawTextFieldContextMenu("ctx_NodeName", ref name))
                {
                    string trimmed = name.Trim();
                    if (!string.Equals(trimmed, node.Name, StringComparison.Ordinal))
                    {
                        ImGuiUndoHelper.TrackDragUndo("Rename Node", node);
                        node.Name = string.IsNullOrWhiteSpace(trimmed) ? SceneNode.DefaultName : trimmed;
                    }
                }
            });

            DrawInspectorRow("Active Self", () =>
            {
                bool active = node.IsActiveSelf;
                if (ImGui.Checkbox("##SceneNodeActiveSelf", ref active))
                {
                    using var _ = Undo.TrackChange("Toggle Active Self", node);
                    node.IsActiveSelf = active;
                }
            });

            DrawInspectorRow("Active In Hierarchy", () =>
            {
                bool active = node.IsActiveInHierarchy;
                if (ImGui.Checkbox("##SceneNodeActiveInHierarchy", ref active))
                {
                    using var _ = Undo.TrackChange("Toggle Active In Hierarchy", node);
                    node.IsActiveInHierarchy = active;
                }
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

        private static void DrawTransformInspector(IReadOnlyList<SceneNode> nodes, HashSet<object> visited)
        {
            _multiTransformScratch.Clear();
            for (int i = 0; i < nodes.Count; i++)
                _multiTransformScratch.Add(nodes[i].Transform);

            DrawTransformInspector(_multiTransformScratch, visited);
        }

        private static void DrawTransformInspector(IReadOnlyList<TransformBase> transforms, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawTransformInspector.Multi");
            if (transforms.Count == 0)
            {
                ImGui.TextDisabled("No transforms selected.");
                return;
            }

            _multiTransformTargetsScratch.Clear();
            Type? commonType = null;
            bool hasMultipleConcreteTypes = false;
            Type? firstConcreteType = null;
            for (int i = 0; i < transforms.Count; i++)
            {
                TransformBase transform = transforms[i];
                _multiTransformTargetsScratch.Add(transform);

                Type concreteType = transform.GetType();
                firstConcreteType ??= concreteType;
                if (firstConcreteType != concreteType)
                    hasMultipleConcreteTypes = true;

                commonType = commonType is null
                    ? concreteType
                    : FindCommonBaseType(commonType, concreteType);
            }

            commonType ??= typeof(TransformBase);
            var targetSet = new InspectorTargetSet(_multiTransformTargetsScratch, commonType);
            if (commonType != typeof(TransformBase))
                ImGui.TextDisabled($"Transform Type: {commonType.Name}");
            else if (hasMultipleConcreteTypes)
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

            // Snapshot components into reusable scratch list (avoids ToArray allocation)
            _componentsScratch.Clear();
            var components = node.Components;
            int componentCount = components.Count;
            for (int i = 0; i < componentCount; i++)
            {
                var c = components[i];
                if (c is not null)
                    _componentsScratch.Add(c);
            }

            bool anyComponentsDrawn = false;
            List<XRComponent>? componentsToRemove = null;

            for (int ci = 0; ci < _componentsScratch.Count; ci++)
            {
                var component = _componentsScratch[ci];

                anyComponentsDrawn = true;
                int componentHash = component.GetHashCode();
                ImGui.PushID(componentHash);

                string typeName = component.GetType().Name;
                string? componentName = string.IsNullOrWhiteSpace(component.Name) ? null : component.Name;

                // Cache label strings keyed by (hash, name, type) to avoid per-frame string allocations
                var labelKey = (componentHash, componentName, typeName);
                if (!_componentLabelCache.TryGetValue(labelKey, out var cachedLabels))
                {
                    string fullDisplay = componentName is null ? typeName : $"{componentName} ({typeName})";
                    string header = $"{fullDisplay}##Component{componentHash}";
                    string renamePopup = $"ComponentRenamePopup##{componentHash}";
                    cachedLabels = (fullDisplay, header, renamePopup);
                    _componentLabelCache[labelKey] = cachedLabels;
                }

                string fullDisplayLabel = cachedLabels.FullDisplay;
                string renamePopupId = cachedLabels.RenamePopup;

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
                    // Use the cached header label when it matches the full display;
                    // only allocate a truncated variant when the label actually needs shortening.
                    string headerLabel;
                    if (componentName is not null)
                    {
                        float fullWidth = ImGui.CalcTextSize(fullDisplayLabel).X;
                        if (fullWidth > headerAvail)
                            headerLabel = $"{componentName}##Component{componentHash}";
                        else
                            headerLabel = cachedLabels.Header;
                    }
                    else
                    {
                        headerLabel = cachedLabels.Header;
                    }
                    ImGuiTreeNodeFlags headerFlags = ImGuiTreeNodeFlags.DefaultOpen;
                    open = ImGui.CollapsingHeader(headerLabel, headerFlags);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(fullDisplayLabel);

                    if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
                    {
                        ImGuiComponentDragDrop.SetPayload(component);
                        ImGui.TextUnformatted(fullDisplayLabel);
                        ImGui.EndDragDropSource();
                    }

                    ImGui.TableSetColumnIndex(1);
                    using (new ImGuiDisabledScope(component.IsDestroyed))
                    {
                        bool active = component.IsActive;
                        if (ImGui.Checkbox("##ComponentActive", ref active))
                        {
                            using var _ = Undo.TrackChange("Toggle Component Active", component);
                            component.IsActive = active;
                        }
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
                    var ownerNode = node;
                    string compName = componentCapture.GetType().Name;

                    EnqueueSceneEdit(() =>
                    {
                        try
                        {
                            if (componentCapture is not null && !componentCapture.IsDestroyed)
                            {
                                ownerNode.DetachComponent(componentCapture);

                                using var interaction = Undo.BeginUserInteraction();
                                using var scope = Undo.BeginChange($"Remove {compName}");
                                Undo.RecordStructuralChange($"Remove {compName}",
                                    undoAction: () =>
                                    {
                                        ownerNode.ReattachComponent(componentCapture);
                                    },
                                    redoAction: () =>
                                    {
                                        ownerNode.DetachComponent(componentCapture);
                                    });
                            }
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

        /// <summary>Scratch dictionaries reused in multi-node DrawComponentInspectors.</summary>
        private static readonly Dictionary<Type, List<XRComponent>> _multiComponentsByType = [];
        private static readonly Dictionary<Type, int> _multiTypeCounts = [];
        private static readonly HashSet<Type> _multiInvalidTypes = [];
        private static readonly List<Type> _multiSharedTypes = [];

        private static void DrawComponentInspectors(IReadOnlyList<SceneNode> nodes, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawComponentInspectors.Multi");
            if (nodes.Count == 0)
            {
                ImGui.TextDisabled("No components available.");
                return;
            }

            _multiComponentsByType.Clear();
            _multiTypeCounts.Clear();
            _multiInvalidTypes.Clear();

            for (int ni = 0; ni < nodes.Count; ni++)
            {
                var nodeComponents = nodes[ni].Components;
                int compCount = nodeComponents.Count;
                for (int ci = 0; ci < compCount; ci++)
                {
                    var c = nodeComponents[ci];
                    if (c is null || c.IsDestroyed)
                        continue;

                    var type = c.GetType();
                    _multiTypeCounts[type] = _multiTypeCounts.TryGetValue(type, out int cnt) ? cnt + 1 : 1;

                    if (_multiInvalidTypes.Contains(type))
                        continue;

                    if (!_multiComponentsByType.TryGetValue(type, out var list))
                    {
                        list = new List<XRComponent>();
                        _multiComponentsByType[type] = list;
                        list.Add(c);
                    }
                    else
                    {
                        // If this node already contributed a component of this type, mark invalid (duplicate per node)
                        bool duplicateForNode = false;
                        for (int li = list.Count - 1; li >= 0; li--)
                        {
                            if (ReferenceEquals(list[li].SceneNode, c.SceneNode))
                            {
                                duplicateForNode = true;
                                break;
                            }
                        }
                        if (duplicateForNode)
                        {
                            _multiInvalidTypes.Add(type);
                        }
                        else
                        {
                            list.Add(c);
                        }
                    }
                }
            }

            _multiSharedTypes.Clear();
            foreach (var kvp in _multiComponentsByType)
            {
                var type = kvp.Key;
                if (!_multiInvalidTypes.Contains(type)
                    && _multiTypeCounts.TryGetValue(type, out int count)
                    && count == nodes.Count
                    && kvp.Value.Count == nodes.Count)
                {
                    _multiSharedTypes.Add(type);
                }
            }
            _multiSharedTypes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            if (_multiSharedTypes.Count == 0)
            {
                ImGui.TextDisabled("No shared components across the selection.");
                return;
            }

            for (int si = 0; si < _multiSharedTypes.Count; si++)
            {
                var type = _multiSharedTypes[si];
                var components = _multiComponentsByType[type];
                DrawMultiComponentInspector(type, components, visited);
                ImGui.Spacing();
            }

            int totalTypes = _multiTypeCounts.Count;
            if (_multiSharedTypes.Count < totalTypes || _multiInvalidTypes.Count > 0)
            {
                ImGui.TextDisabled("Some components are not shared across the selection.");
            }
        }

        private static readonly List<XRComponent> _multiComponentListScratch = [];

        private static void DrawMultiComponentInspector(Type componentType, IReadOnlyList<XRComponent> components, HashSet<object> visited)
        {
            _multiComponentListScratch.Clear();
            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c is not null && !c.IsDestroyed)
                    _multiComponentListScratch.Add(c);
            }
            if (_multiComponentListScratch.Count == 0)
            {
                ImGui.TextDisabled($"No active components of type {componentType.Name}.");
                return;
            }

            int componentHash = componentType.GetHashCode();
            ImGui.PushID(componentHash);

            var labels = GetComponentInspectorLabels(componentType);

            // Check common name without LINQ
            string? commonName = null;
            if (_multiComponentListScratch.Count > 0)
            {
                string firstName = _multiComponentListScratch[0].Name ?? string.Empty;
                bool allSame = true;
                for (int i = 1; i < _multiComponentListScratch.Count; i++)
                {
                    if (((_multiComponentListScratch[i].Name ?? string.Empty) != firstName))
                    {
                        allSame = false;
                        break;
                    }
                }
                if (allSame)
                    commonName = _multiComponentListScratch[0].Name;
            }
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
                    ImGui.SetTooltip($"{componentType.FullName} ({_multiComponentListScratch.Count} selected)");

                ImGui.TableSetColumnIndex(1);
                bool allActive = true, allInactive = true;
                for (int i = 0; i < _multiComponentListScratch.Count; i++)
                {
                    if (_multiComponentListScratch[i].IsActive)
                        allInactive = false;
                    else
                        allActive = false;
                }
                bool mixed = !allActive && !allInactive;
                bool active = allActive;
                if (mixed)
                    ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.6f);
                bool toggled = ImGui.Checkbox("##ComponentActiveMulti", ref active);
                if (mixed)
                    ImGui.PopStyleVar();
                if (toggled)
                {
                    using var interaction = Undo.BeginUserInteraction();
                    using var scope = Undo.BeginChange("Toggle Component Active");
                    for (int i = 0; i < _multiComponentListScratch.Count; i++)
                    {
                        var component = _multiComponentListScratch[i];
                        Undo.Track(component);
                        component.IsActive = active;
                    }
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
                for (int i = 0; i < _multiComponentListScratch.Count; i++)
                {
                    var componentCapture = _multiComponentListScratch[i];
                    var ownerNode = componentCapture.SceneNode;
                    string compName = componentCapture.GetType().Name;

                    EnqueueSceneEdit(() =>
                    {
                        try
                        {
                            if (!componentCapture.IsDestroyed && ownerNode is not null)
                            {
                                ownerNode.DetachComponent(componentCapture);

                                using var interaction = Undo.BeginUserInteraction();
                                using var scope = Undo.BeginChange($"Remove {compName}");
                                Undo.RecordStructuralChange($"Remove {compName}",
                                    undoAction: () =>
                                    {
                                        ownerNode.ReattachComponent(componentCapture);
                                    },
                                    redoAction: () =>
                                    {
                                        ownerNode.DetachComponent(componentCapture);
                                    });
                            }
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

                var targetList = new List<object>(_multiComponentListScratch.Count);
                for (int i = 0; i < _multiComponentListScratch.Count; i++)
                    targetList.Add(_multiComponentListScratch[i]);
                var targetSet = new InspectorTargetSet(targetList, componentType);
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
                using var profilerScope = Engine.Profiler.Start($"UI.ComponentEditor.{component.GetType().Name}");
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
            bool isCurrentlyPossessed = ReferenceEquals(mainPlayer?.ControlledPawnComponent, pawn);

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
            mainPlayer.ControlledPawnComponent = null;

            // Ensure player has a valid viewport
            var window = Engine.Windows.FirstOrDefault();
            if (window is not null)
            {
                var ensuredViewport = window.EnsureControllerRegistered(mainPlayer, autoSizeAllViewports: false);
                if (ensuredViewport is not null)
                {
                    (mainPlayer.InputDevice as LocalInputInterface)?.UpdateDevices(ensuredViewport.Window?.Input, Engine.VRState.Actions);
                }
            }

            // Set the controlled pawn - this triggers UpdateViewportCamera
            mainPlayer.ControlledPawnComponent = pawn;

            // Force refresh the viewport camera binding
            mainPlayer.OnPawnCameraChanged();

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

        internal static void DrawSceneNodeInspectorInline(SceneNode node)
        {
            ArgumentNullException.ThrowIfNull(node);
            DrawSceneNodeInspector(node);
        }

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
