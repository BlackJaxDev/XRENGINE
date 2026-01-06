using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ImGuiNET;
using XREngine;
using XREngine.Animation;
using XREngine.Components;
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
        private static partial void BeginAddComponentForHierarchyNode(SceneNode node)
        {
            _nodePendingAddComponent = node;
            _componentPickerSearch = string.Empty;
            _componentPickerError = null;
            _addComponentPopupOpen = true;
            _addComponentPopupRequested = true;
        }

        private static partial void DrawHierarchyAddComponentPopup()
        {
            if (_nodePendingAddComponent is null && !_addComponentPopupOpen)
                return;

            if (_addComponentPopupRequested)
            {
                ImGui.OpenPopup(HierarchyAddComponentPopupId);
                _addComponentPopupRequested = false;
            }

            ImGui.SetNextWindowSize(new Vector2(640f, 520f), ImGuiCond.FirstUseEver);
            if (ImGui.BeginPopupModal(HierarchyAddComponentPopupId, ref _addComponentPopupOpen, ImGuiWindowFlags.NoCollapse))
            {
                var targetNode = _nodePendingAddComponent;
                if (targetNode is null)
                {
                    ImGui.CloseCurrentPopup();
                    ImGui.EndPopup();
                    return;
                }

                ImGui.TextUnformatted($"Add Component to '{targetNode.Name ?? SceneNode.DefaultName}'");
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
                    if (targetNode.TryAddComponent(requestedComponent, out _))
                    {
                        _componentPickerError = null;
                        closePopup = true;
                    }
                    else
                    {
                        _componentPickerError = $"Unable to add component '{requestedComponent.Name}'.";
                    }
                }

                if (closePopup)
                    CloseComponentPickerPopup();

                ImGui.Separator();

                if (ImGui.Button("Close") || ImGui.IsKeyPressed(ImGuiKey.Escape))
                    CloseComponentPickerPopup();

                ImGui.EndPopup();
            }
            else if (_nodePendingAddComponent is not null)
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

            object? standaloneTarget = _inspectorStandaloneTarget;
            var selectedNode = Selection.SceneNode ?? Selection.LastSceneNode;

            if (standaloneTarget is null && selectedNode is null)
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

                    if (_inspectorStandaloneTarget is null && selectedNode is not null)
                        allowSceneInspector = true;
                }
                else if (selectedNode is not null)
                {
                    allowSceneInspector = true;
                }

                if (allowSceneInspector && selectedNode is not null)
                    DrawSceneNodeInspector(selectedNode);

                ImGui.EndChild();
            }

            DrawHierarchyAddComponentPopup();

            ImGui.End();
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

                float removeButtonWidth = ImGui.CalcTextSize("Remove").X + ImGui.GetStyle().FramePadding.X * 2f;
                float availableWidth = ImGui.GetContentRegionAvail().X;
                float firstColumnWidth = MathF.Max(0f, availableWidth - removeButtonWidth - ImGui.GetStyle().ItemSpacing.X);

                ImGui.Columns(2, null, false);
                ImGui.SetColumnWidth(0, firstColumnWidth);
                ImGui.SetColumnWidth(1, removeButtonWidth);

                string headerLabel = $"{component.GetType().Name}##Component{componentHash}";
                ImGuiTreeNodeFlags headerFlags = ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth;
                bool open = ImGui.CollapsingHeader(headerLabel, headerFlags);

                ImGui.NextColumn();
                using (new ImGuiDisabledScope(component.IsDestroyed))
                {
                    if (ImGui.SmallButton("Remove"))
                    {
                        componentsToRemove ??= new List<XRComponent>();
                        componentsToRemove.Add(component);
                    }
                }

                ImGui.NextColumn();
                ImGui.Columns(1);

                if (open)
                {
                    DrawComponentInspector(component, visited);
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
        _nodePendingAddComponent = null;
        _componentPickerError = null;
        _componentPickerSearch = string.Empty;
        _addComponentPopupRequested = false;
    }
}
