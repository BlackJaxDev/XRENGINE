using ImGuiNET;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.IO;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Editor.ComponentEditors;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
        public sealed class InspectorTargetSet
        {
            public InspectorTargetSet(IReadOnlyList<object> targets, Type commonType)
            {
                Targets = targets;
                CommonType = commonType;
            }

            public IReadOnlyList<object> Targets { get; }
            public Type CommonType { get; }
            public object PrimaryTarget => Targets[0];
            public bool HasMultipleTargets => Targets.Count > 1;
        }

        private static InspectorTargetSet CreateInspectorTargetSet(IEnumerable<object> targets)
        {
            var targetList = targets.Where(t => t is not null).ToList();
            if (targetList.Count == 0)
                throw new ArgumentException("Inspector target list must contain at least one object.", nameof(targets));

            var commonType = FindCommonBaseType(targetList.Select(t => t.GetType())) ?? typeof(object);
            return new InspectorTargetSet(targetList, commonType);
        }

        private static Type? FindCommonBaseType(IEnumerable<Type> types)
        {
            using var enumerator = types.GetEnumerator();
            if (!enumerator.MoveNext())
                return null;

            Type current = enumerator.Current;
            while (enumerator.MoveNext())
            {
                current = FindCommonBaseType(current, enumerator.Current);
                if (current == typeof(object))
                    break;
            }

            return current;
        }

        private static Type FindCommonBaseType(Type first, Type second)
        {
            if (first.IsAssignableFrom(second))
                return first;
            if (second.IsAssignableFrom(first))
                return second;

            // Interfaces have higher priority than object when both types share one.
            foreach (var iface in first.GetInterfaces())
            {
                if (iface.IsAssignableFrom(second))
                    return iface;
            }

            Type? baseType = first.BaseType;
            while (baseType is not null)
            {
                if (baseType.IsAssignableFrom(second))
                    return baseType;
                baseType = baseType.BaseType;
            }

            return typeof(object);
        }

        private static readonly NullabilityInfoContext _nullabilityContext = new();

        private static bool IsPropertyNullable(PropertyInfo property)
        {
            Type type = property.PropertyType;
            if (type.IsValueType)
                return Nullable.GetUnderlyingType(type) is not null;

            try
            {
                var info = _nullabilityContext.Create(property);
                return info.ReadState != NullabilityState.NotNull;
            }
            catch
            {
                // If nullability metadata isn't available (e.g. legacy assemblies), fall back to the
                // historical behavior: treat reference types as nullable.
                return true;
            }
        }

        internal static bool HasCreatablePropertyTypes(Type baseType)
            => GetPropertyTypeDescriptors(baseType).Count > 0;

        internal static void DrawCreatablePropertyTypePickerPopup(string popupId, Type baseType, Action<Type> onSelected)
            => DrawPropertyTypePickerPopup(popupId, baseType, onSelected);
        private static string FormatFlagsEnumPreview(Type enumType, Array values, ulong bits)
        {
            if (bits == 0)
                return "None";

            var names = new List<string>();
            for (int i = 0; i < values.Length; i++)
            {
                object raw = values.GetValue(i)!;
                ulong flag;
                try
                {
                    flag = Convert.ToUInt64(raw, CultureInfo.InvariantCulture);
                }
                catch
                {
                    continue;
                }

                if (flag == 0)
                    continue;

                if ((bits & flag) == flag)
                {
                    string name = Enum.GetName(enumType, raw) ?? raw.ToString() ?? $"0x{flag:X}";
                    names.Add(name);
                }
            }

            if (names.Count == 0)
                return bits == 0 ? "None" : "Custom";

            // Avoid overly long preview strings.
            const int maxPreviewChars = 64;
            string joined = string.Join(", ", names);
            if (joined.Length <= maxPreviewChars)
                return joined;

            return $"{names.Count} flags";
        }

        // Property Type Picker state
        private static readonly Dictionary<Type, List<CollectionTypeDescriptor>> _propertyTypeDescriptorCache = new();
        private static readonly Dictionary<string, string> _propertyTypePickerSearch = new(StringComparer.Ordinal);

        // Property Editor Logic will be moved here
        private static void DrawSettingsObject(InspectorTargetSet targets, string label, string? description, HashSet<object> visited, bool defaultOpen, string? idOverride = null)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSettingsObject");

            object primary = targets.PrimaryTarget;
            if (!visited.Add(primary))
            {
                ImGui.TextUnformatted($"{label}: <circular reference>");
                return;
            }

            if (primary is XRRenderPipelineInstance instance && instance.Pipeline is not null)
            {
                if (ImGui.SmallButton($"Open Pipeline Graph##{label}"))
                    OpenRenderPipelineGraph(instance.Pipeline);
                ImGui.SameLine();
                ImGui.TextDisabled(instance.DebugDescriptor);
            }

            string id = idOverride ?? label;
            ImGui.PushID(id);

            string typeDisplay = targets.CommonType.Name;
            string treeLabel = targets.HasMultipleTargets
                ? $"{label} ({typeDisplay}, {targets.Targets.Count} selected)"
                : $"{label} ({primary.GetType().Name})";

            bool open = ImGui.TreeNodeEx(treeLabel, defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);
            if (open)
            {
                if (primary is Mipmap2D mip)
                {
                    DrawMipmap2DInspector(mip);
                    ImGui.Separator();
                }

                bool handledByAssetInspector = false;
                if (primary is XRAsset asset)
                {
                    using var assetScope = new InspectorAssetContextScope(asset.SourceAsset ?? asset);

                    // Allow custom inspectors to draw even if this asset is already tracked in the visited set.
                    visited.Remove(primary);
                    try
                    {
                        handledByAssetInspector = TryDrawAssetInspector(targets, visited);
                    }
                    finally
                    {
                        visited.Add(primary);
                    }
                }

                if (!handledByAssetInspector)
                    DrawSettingsProperties(targets, visited);

                ImGui.TreePop();
            }
            ImGui.PopID();
            visited.Remove(primary);
        }

        internal static void DrawRuntimeObjectInspector(string label, object? target, HashSet<object> visited, bool defaultOpen = true, string? description = null)
        {
            if (target is null)
            {
                ImGui.TextDisabled($"{label}: <null>");
                return;
            }

            DrawRuntimeObjectInspector(label, new[] { target }, visited, defaultOpen, description);
        }

        internal static void DrawRuntimeObjectInspector(string label, IReadOnlyList<object> targets, HashSet<object> visited, bool defaultOpen = true, string? description = null)
        {
            if (targets.Count == 0)
            {
                ImGui.TextDisabled($"{label}: <none>");
                return;
            }

            DrawSettingsObject(CreateInspectorTargetSet(targets), label, description, visited, defaultOpen);
        }

        private static void DrawSettingsProperties(InspectorTargetSet targets, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSettingsProperties");
            object primary = targets.PrimaryTarget;
            Type type = targets.CommonType;

            string search = _inspectorPropertySearch ?? string.Empty;
            bool hasSearch = !string.IsNullOrWhiteSpace(search);
            var propertyInfos = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .Select(p =>
                {
                    var displayAttr = p.GetCustomAttribute<DisplayNameAttribute>();
                    var descAttr = p.GetCustomAttribute<DescriptionAttribute>();
                    var categoryAttr = p.GetCustomAttribute<CategoryAttribute>();
                    var browsableAttr = p.GetCustomAttribute<BrowsableAttribute>();

                    if (browsableAttr != null && !browsableAttr.Browsable)
                        return null;

                    string displayName = displayAttr?.DisplayName ?? p.Name;
                    string? description = descAttr?.Description;
                    string? category = categoryAttr?.Category;

                    var values = new List<object?>();
                    bool valueRetrievalFailed = false;
                    foreach (var target in targets.Targets)
                    {
                        try
                        {
                            values.Add(p.GetValue(target));
                        }
                        catch
                        {
                            values.Add(null);
                            valueRetrievalFailed = true;
                        }
                    }

                    bool isOverrideable = typeof(IOverrideableSetting).IsAssignableFrom(p.PropertyType);
                    bool isSimple = isOverrideable || IsSimpleSettingType(p.PropertyType);

                    return new SettingPropertyDescriptor
                    {
                        Property = p,
                        Values = values,
                        ValueRetrievalFailed = valueRetrievalFailed,
                        IsSimple = isSimple,
                        Category = category,
                        DisplayName = displayName,
                        Description = description,
                        IsOverrideable = isOverrideable
                    };
                })
                .Where(x => x != null)
                .Select(x => x!)
                .ToList();

            var fieldInfos = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => !f.IsStatic && !f.IsLiteral)
                .Select(f =>
                {
                    var displayAttr = f.GetCustomAttribute<DisplayNameAttribute>();
                    var descAttr = f.GetCustomAttribute<DescriptionAttribute>();
                    var categoryAttr = f.GetCustomAttribute<CategoryAttribute>();
                    var browsableAttr = f.GetCustomAttribute<BrowsableAttribute>();

                    if (browsableAttr != null && !browsableAttr.Browsable)
                        return null;

                    // Support the engine's Unity-style attribute even if it's not used widely yet.
                    if (f.GetCustomAttribute<HideInInspectorAttribute>() is not null)
                        return null;

                    string displayName = displayAttr?.DisplayName ?? f.Name;
                    string? description = descAttr?.Description;
                    string? category = categoryAttr?.Category;

                    var values = new List<object?>();
                    bool valueRetrievalFailed = false;
                    foreach (var target in targets.Targets)
                    {
                        try
                        {
                            values.Add(f.GetValue(target));
                        }
                        catch
                        {
                            values.Add(null);
                            valueRetrievalFailed = true;
                        }
                    }

                    bool isSimple = IsSimpleSettingType(f.FieldType);
                    bool canWrite = !f.IsInitOnly;

                    return new SettingFieldDescriptor
                    {
                        Field = f,
                        Values = values,
                        ValueRetrievalFailed = valueRetrievalFailed,
                        IsSimple = isSimple,
                        Category = category,
                        DisplayName = displayName,
                        Description = description,
                        CanWrite = canWrite
                    };
                })
                .Where(x => x != null)
                .Select(x => x!)
                .ToList();

            // Pair overrideable settings with their base property (e.g., VSync + VSyncOverride)
            if (propertyInfos.Count > 0)
            {
                var propertyMap = propertyInfos.ToDictionary(info => info.Property.Name, info => info, StringComparer.Ordinal);

                foreach (SettingPropertyDescriptor info in propertyInfos)
                {
                    if (!info.IsOverrideable)
                        continue;

                    string propertyName = info.Property.Name;
                    const string overrideSuffix = "Override";
                    if (!propertyName.EndsWith(overrideSuffix, StringComparison.Ordinal))
                        continue;

                    string baseName = propertyName[..^overrideSuffix.Length];
                    if (!propertyMap.TryGetValue(baseName, out var baseInfo))
                        continue;

                    info.PairedBaseProperty = baseInfo.Property;
                    if (string.IsNullOrWhiteSpace(info.Description))
                        info.Description = baseInfo.Description;

                    // Hide the base property row; its editing is integrated into the override row.
                    baseInfo.Hidden = true;

                    // Prefer the base property's display name for the combined row to reduce duplication.
                    info.DisplayName = baseInfo.DisplayName;
                }
            }

            var allRows = new List<InspectorMemberRow>(propertyInfos.Count + fieldInfos.Count);
            allRows.AddRange(propertyInfos.Select(p => new InspectorMemberRow(p)));
            allRows.AddRange(fieldInfos.Select(f => new InspectorMemberRow(f)));

            if (allRows.Count == 0)
            {
                ImGui.TextDisabled("No properties or fields found.");
                return;
            }

            var orderedRows = allRows
                .Where(row => !row.Hidden)
                .Where(row =>
                {
                    if (!hasSearch)
                        return true;

                    return row.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || row.MemberName.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || (row.Category?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
                })
                .OrderBy(row => string.IsNullOrWhiteSpace(row.Category) ? 0 : 1)
                .ThenBy(row => row.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (orderedRows.Count == 0)
            {
                ImGui.TextDisabled(hasSearch ? "No properties match the current search." : "No properties found.");
                return;
            }

            var grouped = orderedRows
                .GroupBy(row => row.Category, StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool showCategoryHeaders = grouped.Count > 1;

            bool renderedCategoryHeader = false;

            foreach (var group in grouped)
            {
                string categoryLabel = string.IsNullOrWhiteSpace(group.Key) ? "General" : group.Key;

                var simpleRows = group.Where(row => row.IsSimple).ToList();
                var complexRows = group.Where(row => !row.IsSimple).ToList();

                if (showCategoryHeaders)
                {
                    if (renderedCategoryHeader)
                        ImGui.Separator();

                    // Collapse categories by default, but auto-expand while searching to show matches.
                    ImGui.SetNextItemOpen(hasSearch, hasSearch ? ImGuiCond.Always : ImGuiCond.Once);
                    bool categoryOpen = ImGui.CollapsingHeader($"{categoryLabel}##InspectorCategory_{group.Key}", ImGuiTreeNodeFlags.SpanAvailWidth);
                    renderedCategoryHeader = true;

                    if (!categoryOpen)
                        continue;
                }

                if (simpleRows.Count > 0)
                {
                    string tableId = $"Properties_{primary.GetHashCode():X8}_{group.Key?.GetHashCode() ?? 0:X8}";
                    if (ImGui.BeginTable(tableId, 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
                    {
                        // Prevent the name column from collapsing to a few pixels.
                        // Without this, long value widgets can steal all width and labels get clipped to 1-2 chars.
                        ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthFixed, 280.0f);
                        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
                        foreach (var row in simpleRows)
                        {
                            if (row.Property is not null)
                                DrawSimplePropertyRow(targets, row.Property.Property, row.Property.Values, row.DisplayName, row.Description, row.Property.ValueRetrievalFailed);
                            else if (row.Field is not null)
                                DrawSimpleFieldRow(targets, row.Field.Field, row.Field.Values, row.DisplayName, row.Description, row.Field.ValueRetrievalFailed, row.Field.CanWrite);
                        }
                        ImGui.EndTable();
                    }
                }

                foreach (var row in complexRows)
                {
                    if (row.ValueRetrievalFailed)
                    {
                        ImGui.TextUnformatted($"{row.DisplayName}: <error>");
                        if (!string.IsNullOrEmpty(row.Description) && ImGui.IsItemHovered())
                            ImGui.SetTooltip(row.Description);
                        continue;
                    }

                    if (targets.HasMultipleTargets)
                    {
                        ImGui.TextDisabled($"{row.DisplayName}: <multiple values>");
                        continue;
                    }

                    object? value = row.Values.FirstOrDefault();

                    if (TryDrawXREventMember(primary, row, value, visited))
                        continue;

                    if (value is null)
                    {
                        if (row.Property is not null)
                            DrawNullComplexProperty(primary, row.Property.Property, row.DisplayName, row.Description);
                        else if (row.Field is not null)
                            DrawNullComplexField(primary, row.Field.Field, row.DisplayName, row.Description, row.Field.CanWrite);
                        continue;
                    }

                    if (row.Property is not null)
                    {
                        if (TryDrawCollectionProperty(primary, row.Property.Property, row.DisplayName, row.Description, value, visited))
                            continue;

                        // Handle GL objects with their custom ImGui editors
                        if (TryDrawGLObjectProperty(row.Property.Property, row.DisplayName, row.Description, value))
                            continue;

                        DrawComplexPropertyObject(primary, row.Property.Property, value, row.DisplayName, row.Description, visited);
                    }
                    else if (row.Field is not null)
                    {
                        // Field fallback: just draw the object inspector for the field value.
                        DrawComplexFieldObject(primary, row.Field.Field, value, row.DisplayName, row.Description, visited);
                    }
                }
            }
        }

        private sealed class InspectorMemberRow
        {
            public InspectorMemberRow(SettingPropertyDescriptor property)
            {
                Property = property;
                DisplayName = property.DisplayName;
                Description = property.Description;
                Category = property.Category;
                IsSimple = property.IsSimple;
                Values = property.Values;
                ValueRetrievalFailed = property.ValueRetrievalFailed;
                Hidden = property.Hidden;
            }

            public InspectorMemberRow(SettingFieldDescriptor field)
            {
                Field = field;
                DisplayName = field.DisplayName;
                Description = field.Description;
                Category = field.Category;
                IsSimple = field.IsSimple;
                Values = field.Values;
                ValueRetrievalFailed = field.ValueRetrievalFailed;
                Hidden = field.Hidden;
            }

            public SettingPropertyDescriptor? Property { get; }
            public SettingFieldDescriptor? Field { get; }

            public string DisplayName { get; }
            public string? Description { get; }
            public string? Category { get; }
            public bool IsSimple { get; }
            public bool ValueRetrievalFailed { get; }
            public IReadOnlyList<object?> Values { get; }
            public bool Hidden { get; }

            public string MemberName
                => Property?.Property.Name ?? Field?.Field.Name ?? string.Empty;
        }

        /// <summary>
        /// Draws a complex property object with support for clearing the value if nullable.
        /// </summary>
        private static void DrawComplexPropertyObject(object owner, PropertyInfo property, object value, string label, string? description, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawComplexPropertyObject");
            if (!visited.Add(value))
            {
                ImGui.TextUnformatted($"{label}: <circular reference>");
                return;
            }

            Type propertyType = property.PropertyType;
            bool isNullable = Nullable.GetUnderlyingType(propertyType) is not null || !propertyType.IsValueType;
            bool canWrite = property.CanWrite && property.SetMethod?.IsPublic == true;

            ImGui.PushID(property.Name);
            string treeLabel = $"{label} ({value.GetType().Name})";
            bool open = ImGui.TreeNodeEx(treeLabel, ImGuiTreeNodeFlags.None);

            // Add Clear button for nullable, writable properties
            if (isNullable && canWrite)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear"))
                {
                    try
                    {
                        property.SetValue(owner, null);
                        NotifyInspectorValueEdited(owner);
                        ImGui.TreePop();
                        ImGui.PopID();
                        visited.Remove(value);
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, $"Failed to clear property '{property.Name}'.");
                    }
                }
            }

            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);

            if (open)
            {
                bool handledByAssetInspector = false;
                if (value is XRAsset asset)
                {
                    using var assetScope = new InspectorAssetContextScope(asset.SourceAsset ?? asset);

                    // Allow custom inspectors to draw even if this asset is already tracked in the visited set.
                    visited.Remove(value);
                    try
                    {
                        handledByAssetInspector = TryDrawAssetInspector(new InspectorTargetSet(new[] { asset }, asset.GetType()), visited);
                    }
                    finally
                    {
                        visited.Add(value);
                    }
                }

                if (!handledByAssetInspector)
                    DrawSettingsProperties(new InspectorTargetSet(new[] { value }, value.GetType()), visited);

                ImGui.TreePop();
            }
            ImGui.PopID();
            visited.Remove(value);
        }

        /// <summary>
        /// Attempts to draw a GL object property using the attribute-based editor registry.
        /// </summary>
        /// <param name="property">The property info.</param>
        /// <param name="label">The display label.</param>
        /// <param name="description">Optional description for tooltip.</param>
        /// <param name="value">The GL object instance.</param>
        /// <returns>True if the property was handled as a GL object.</returns>
        private static bool TryDrawGLObjectProperty(PropertyInfo property, string label, string? description, object value)
        {
            if (value is not OpenGLRenderer.GLObjectBase glObject)
                return false;

            ImGui.PushID(property.Name);

            bool open = ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen);
            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);

            if (open)
            {
                // Use the attribute-based registry to draw the appropriate editor
                GLObjectEditorRegistry.DrawInspector(glObject);
                ImGui.TreePop();
            }

            ImGui.PopID();
            return true;
        }

        private static bool TryDrawCollectionProperty(object? owner, PropertyInfo property, string label, string? description, object value, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.TryDrawCollectionProperty");

            if (value is IDictionary dictionary)
                return DrawDictionaryProperty(owner, property, label, description, dictionary, visited);

            Type declaredElementType = GetCollectionElementType(property, value.GetType()) ?? typeof(object);
            Type effectiveDeclaredType = Nullable.GetUnderlyingType(declaredElementType) ?? declaredElementType;

            ImGuiEditorUtilities.CollectionEditorAdapter adapter;

            if (value is Array arrayValue)
            {
                Func<Array, bool>? applyReplacement = null;
                if (property.CanWrite && property.SetMethod?.IsPublic == true)
                {
                    applyReplacement = replacement =>
                    {
                        try
                        {
                            property.SetValue(owner, replacement);
                            NotifyInspectorValueEdited(owner);
                            return true;
                        }
                        catch
                        {
                            return false;
                        }
                    };
                }

                adapter = ImGuiEditorUtilities.CollectionEditorAdapter.ForArray(arrayValue, declaredElementType, applyReplacement);
            }
            else
            {
                IList? list = value as IList;

                if (list is null && TryCreateLinkedListAdapter(value, out var linkedListAdapter))
                    list = linkedListAdapter;

                if (list is null && TryCreateCollectionBufferAdapter(value, declaredElementType, out var bufferAdapter))
                    list = bufferAdapter;

                if (list is null)
                    return false;

                adapter = ImGuiEditorUtilities.CollectionEditorAdapter.ForList(list, declaredElementType);
            }

            bool elementIsAsset = typeof(XRAsset).IsAssignableFrom(effectiveDeclaredType);
            bool elementUsesTypeSelector = ShouldUseCollectionTypeSelector(declaredElementType);
            IReadOnlyList<CollectionTypeDescriptor> availableTypeOptions = elementIsAsset || elementUsesTypeSelector
                ? GetCollectionTypeDescriptors(declaredElementType)
                : Array.Empty<CollectionTypeDescriptor>();
            string headerLabel = $"{label} [{adapter.Count}]";

            ImGui.PushID(property.Name);
            bool open = ImGui.TreeNodeEx(headerLabel, ImGuiTreeNodeFlags.DefaultOpen);
            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);

            if (open)
            {
                if (adapter.Count == 0)
                {
                    ImGui.TextDisabled("<empty>");
                }
                else
                {
                    for (int i = 0; i < adapter.Count; i++)
                    {
                        IList currentList = adapter.Items;

                        object? item;
                        try
                        {
                            item = currentList[i];
                        }
                        catch (Exception ex)
                        {
                            ImGui.TextUnformatted($"[{i}] <error: {ex.Message}>");
                            continue;
                        }

                        Type? runtimeType = item?.GetType();
                        bool itemIsAsset = runtimeType is not null
                            ? typeof(XRAsset).IsAssignableFrom(runtimeType)
                            : elementIsAsset;
                        bool itemUsesTypeSelector = runtimeType is not null
                            ? ShouldUseCollectionTypeSelector(runtimeType)
                            : elementUsesTypeSelector;

                        ImGui.PushID(i);

                        // Draw index label and action buttons on a header row above the item content.
                        ImGui.TextUnformatted($"[{i}]");

                        if (adapter.CanAddRemove)
                        {
                            ImGui.SameLine(0f, 6f);
                            if (ImGui.SmallButton("Remove"))
                            {
                                if (adapter.TryRemoveAt(i))
                                {
                                    NotifyInspectorValueEdited(owner);
                                    ImGui.PopID();
                                    i--;
                                    continue;
                                }
                            }
                        }

                        int availableTypeCount = availableTypeOptions.Count;

                        if (adapter.CanAddRemove && itemUsesTypeSelector && availableTypeCount > 0)
                        {
                            ImGui.SameLine(0f, 6f);
                            if (ImGui.SmallButton("Replace"))
                            {
                                if (availableTypeCount == 1)
                                {
                                    TryReplaceCollectionInstance(adapter, i, availableTypeOptions[0].Type, owner);
                                }
                                else
                                {
                                    ImGui.OpenPopup("ReplaceElement");
                                }
                            }

                            if (availableTypeCount > 1)
                            {
                                DrawCollectionTypePickerPopup("ReplaceElement", declaredElementType, runtimeType, selectedType =>
                                {
                                    TryReplaceCollectionInstance(adapter, i, selectedType, owner);
                                });
                            }
                        }

                        // Export embedded assets into standalone files.
                        // Only show for XRAssets that are currently embedded in the root asset being inspected.
                        if (adapter.CanAddRemove && item is XRAsset embeddedAsset && _inspectorAssetContext is not null)
                        {
                            XRAsset inspectorRoot = _inspectorAssetContext;
                            bool isEmbeddedInInspectorRoot =
                                !ReferenceEquals(embeddedAsset.SourceAsset, embeddedAsset)
                                && ReferenceEquals(embeddedAsset.SourceAsset, inspectorRoot);

                            if (isEmbeddedInInspectorRoot)
                            {
                                ImGui.SameLine(0f, 6f);
                                if (ImGui.SmallButton("Export"))
                                {
                                    string dialogId = $"ExportEmbeddedAsset_{inspectorRoot.ID}_{property.Name}_{i}";
                                    string assetsRoot = Engine.Assets.GameAssetsPath;

                                    ImGuiFileBrowser.SelectFolder(
                                        dialogId,
                                        "Export Embedded Asset",
                                        result =>
                                        {
                                            if (!result.Success || string.IsNullOrWhiteSpace(result.SelectedPath))
                                                return;

                                            try
                                            {
                                                string selectedDir = Path.GetFullPath(result.SelectedPath);
                                                string rootDir = Path.GetFullPath(assetsRoot);

                                                if (!IsPathUnderDirectory(selectedDir, rootDir))
                                                {
                                                    Debug.LogWarning($"Export folder must be inside the Assets directory. Selected='{selectedDir}', AssetsRoot='{rootDir}'");
                                                    return;
                                                }

                                                Engine.Assets.SaveToImmediate(embeddedAsset, selectedDir);

                                                // Rebuild the inspector root's embedded graph so the exported asset is no longer treated as embedded.
                                                XRAssetGraphUtility.RefreshAssetGraph(inspectorRoot);
                                                NotifyInspectorValueEdited(inspectorRoot);
                                            }
                                            catch (Exception ex)
                                            {
                                                Debug.LogException(ex, $"Failed to export embedded asset '{embeddedAsset?.Name ?? embeddedAsset?.GetType().Name ?? "<unknown>"}'.");
                                            }
                                        },
                                        initialDirectory: assetsRoot);
                                }
                            }
                        }

                        // Item content rendered below the header buttons.
                        ImGui.Indent();

                        bool elementCanModify = currentList is Array || !currentList.IsReadOnly;

                        if (itemIsAsset)
                        {
                            DrawCollectionAssetElement(declaredElementType, runtimeType, item as XRAsset, adapter, i, owner);
                        }
                        else if (item is null)
                        {
                            if (runtimeType is not null && IsSimpleSettingType(runtimeType))
                            {
                                object? currentValue = item;
                                DrawCollectionSimpleElement(currentList, runtimeType, i, ref currentValue, elementCanModify);
                            }
                            else if (IsSimpleSettingType(effectiveDeclaredType))
                            {
                                object? currentValue = item;
                                DrawCollectionSimpleElement(currentList, effectiveDeclaredType, i, ref currentValue, elementCanModify);
                            }
                            else
                            {
                                ImGui.TextDisabled("<null>");
                            }
                        }
                        else if (runtimeType is not null && IsSimpleSettingType(runtimeType))
                        {
                            object? currentValue = item;
                            DrawCollectionSimpleElement(currentList, runtimeType, i, ref currentValue, elementCanModify);
                        }
                        else
                        {
                            DrawSettingsObject(new InspectorTargetSet(new[] { item }, item.GetType()), $"{label}[{i}]", description, visited, false, property.Name + i.ToString(CultureInfo.InvariantCulture));
                        }

                        if (item is null && adapter.CanAddRemove && itemUsesTypeSelector && availableTypeCount > 0)
                        {
                            ImGui.SameLine(0f, 6f);
                            if (ImGui.SmallButton("Create"))
                            {
                                if (availableTypeCount == 1)
                                {
                                    TryReplaceCollectionInstance(adapter, i, availableTypeOptions[0].Type, owner);
                                }
                                else
                                {
                                    ImGui.OpenPopup("CreateElement");
                                }
                            }

                            if (availableTypeCount > 1)
                            {
                                DrawCollectionTypePickerPopup("CreateElement", declaredElementType, null, selectedType =>
                                {
                                    TryReplaceCollectionInstance(adapter, i, selectedType, owner);
                                });
                            }
                        }

                        ImGui.Unindent();
                        ImGui.Separator();
                        ImGui.PopID();
                    }
                }

                if (adapter.CanAddRemove)
                {
                    if (elementIsAsset)
                    {
                        int assetTypeCount = availableTypeOptions.Count;

                        if (assetTypeCount == 0)
                        {
                            using (new ImGuiDisabledScope(true))
                                ImGui.Button($"Create Asset##{property.Name}");
                        }
                        else
                        {
                            string createLabel = assetTypeCount == 1
                                ? $"Create {availableTypeOptions[0].DisplayName}##{property.Name}"
                                : $"Create Asset##{property.Name}";

                            if (ImGui.Button(createLabel))
                            {
                                if (assetTypeCount == 1)
                                {
                                    TryAddCollectionInstance(adapter, availableTypeOptions[0].Type, owner);
                                }
                                else
                                {
                                    ImGui.OpenPopup("CreateAssetElement");
                                }
                            }

                            if (assetTypeCount > 1)
                            {
                                DrawCollectionTypePickerPopup("CreateAssetElement", declaredElementType, null, selectedType =>
                                {
                                    TryAddCollectionInstance(adapter, selectedType, owner);
                                });
                            }

                            ImGui.SameLine(0f, 6f);
                            if (ImGui.Button($"Pick Asset##{property.Name}"))
                                ImGui.OpenPopup("AddAssetElement");

                            DrawCollectionAssetAddPopup("AddAssetElement", adapter, owner, availableTypeOptions);
                        }
                    }
                    else if (elementUsesTypeSelector)
                    {
                        int typeCount = availableTypeOptions.Count;

                        if (typeCount == 0)
                        {
                            using (new ImGuiDisabledScope(true))
                                ImGui.Button($"Add Element##{property.Name}");
                        }
                        else if (typeCount == 1)
                        {
                            string typeLabel = $"Add {availableTypeOptions[0].DisplayName}##{property.Name}";
                            if (ImGui.Button(typeLabel))
                                TryAddCollectionInstance(adapter, availableTypeOptions[0].Type, owner);
                        }
                        else
                        {
                            if (ImGui.Button($"Add {GetFriendlyCollectionTypeName(effectiveDeclaredType)}##{property.Name}"))
                                ImGui.OpenPopup("AddCollectionElement");

                            DrawCollectionTypePickerPopup("AddCollectionElement", declaredElementType, null, selectedType =>
                            {
                                TryAddCollectionInstance(adapter, selectedType, owner);
                            });
                        }
                    }
                    else
                    {
                        if (ImGui.Button($"Add Element##{property.Name}"))
                        {
                            object? newElement = adapter.CreateDefaultElement();
                            if (adapter.TryAdd(newElement))
                                NotifyInspectorValueEdited(owner);
                        }
                    }
                }
                else
                {
                    using (new ImGuiDisabledScope(true))
                    {
                        ImGui.Button($"Add Element##{property.Name}");
                    }
                }

                ImGui.TreePop();
            }

            ImGui.PopID();
            return true;
        }

        private static bool DrawDictionaryProperty(object? owner, PropertyInfo property, string label, string? description, IDictionary dictionary, HashSet<object> visited)
        {
            var (keyType, valueType) = ResolveDictionaryTypes(property.PropertyType, dictionary.GetType());
            Type effectiveValueType = Nullable.GetUnderlyingType(valueType) ?? valueType;

            bool valueIsAsset = typeof(XRAsset).IsAssignableFrom(effectiveValueType);
            bool valueUsesTypeSelector = ShouldUseCollectionTypeSelector(valueType);
            IReadOnlyList<CollectionTypeDescriptor> availableTypeOptions = valueIsAsset || valueUsesTypeSelector
                ? GetCollectionTypeDescriptors(valueType)
                : Array.Empty<CollectionTypeDescriptor>();

            string headerLabel = $"{label} [{dictionary.Count}]";
            ImGui.PushID(property.Name);
            bool open = ImGui.TreeNodeEx(headerLabel, ImGuiTreeNodeFlags.DefaultOpen);
            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);

            if (open)
            {
                bool canMutate = !dictionary.IsReadOnly;
                using (new ImGuiDisabledScope(!canMutate))
                {
                    if (ImGui.Button("Add Entry") && canMutate)
                        TryAddDictionaryEntry(dictionary, keyType, valueType, owner);
                }

                var keys = dictionary.Keys.Cast<object?>().ToList();
                if (keys.Count == 0)
                {
                    ImGui.TextDisabled("<empty>");
                }
                else if (ImGui.BeginTable("DictionaryItems", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
                {
                    for (int i = 0; i < keys.Count; i++)
                    {
                        object? key = keys[i];
                        object? entryValue = key is not null && DictionaryContainsKey(dictionary, key) ? dictionary[key] : null;
                        Type? runtimeType = entryValue?.GetType();
                        bool entryIsAsset = runtimeType is not null
                            ? typeof(XRAsset).IsAssignableFrom(runtimeType)
                            : valueIsAsset;
                        bool entryUsesTypeSelector = runtimeType is not null
                            ? ShouldUseCollectionTypeSelector(runtimeType)
                            : valueUsesTypeSelector;

                        ImGui.TableNextRow();
                        ImGui.PushID(i);

                        ImGui.TableSetColumnIndex(0);
                        ImGui.TextUnformatted(FormatDictionaryKey(key));

                        if (canMutate)
                        {
                            ImGui.SameLine(0f, 6f);
                            if (ImGui.SmallButton("Remove"))
                            {
                                if (TryRemoveDictionaryEntry(dictionary, key))
                                    NotifyInspectorValueEdited(owner);
                                ImGui.PopID();
                                continue;
                            }
                        }

                        if (canMutate && entryUsesTypeSelector && availableTypeOptions.Count > 0)
                        {
                            ImGui.SameLine(0f, 6f);
                            string replacePopupId = $"ReplaceDictEntry_{property.Name}_{i}";
                            if (ImGui.SmallButton("Replace"))
                                ImGui.OpenPopup(replacePopupId);

                            DrawCollectionTypePickerPopup(replacePopupId, valueType, runtimeType, selectedType =>
                            {
                                if (TryReplaceDictionaryInstance(dictionary, key, selectedType, owner))
                                    NotifyInspectorValueEdited(owner);
                            });
                        }

                        ImGui.TableSetColumnIndex(1);

                        if (entryIsAsset)
                        {
                            DrawDictionaryAssetElement(property, valueType, runtimeType, dictionary, key, entryValue as XRAsset, owner);
                        }
                        else if (entryValue is null && IsSimpleSettingType(valueType))
                        {
                            object? currentValue = null;
                            DrawDictionarySimpleElement(dictionary, key, valueType, ref currentValue, canMutate, owner);
                        }
                        else if (entryValue is null)
                        {
                            ImGui.TextDisabled("<null>");

                            if (canMutate && entryUsesTypeSelector && availableTypeOptions.Count > 0)
                            {
                                ImGui.SameLine(0f, 6f);
                                string createPopupId = $"CreateDictEntry_{property.Name}_{i}";
                                if (ImGui.SmallButton("Create"))
                                    ImGui.OpenPopup(createPopupId);

                                DrawCollectionTypePickerPopup(createPopupId, valueType, null, selectedType =>
                                {
                                    if (TryReplaceDictionaryInstance(dictionary, key, selectedType, owner))
                                        NotifyInspectorValueEdited(owner);
                                });
                            }
                        }
                        else if (runtimeType is not null && IsSimpleSettingType(runtimeType))
                        {
                            object? currentValue = entryValue;
                            DrawDictionarySimpleElement(dictionary, key, runtimeType, ref currentValue, canMutate, owner);
                        }
                        else if (entryValue is not null)
                        {
                            string childLabel = $"{label}[{FormatDictionaryKey(key)}]";
                            DrawSettingsObject(new InspectorTargetSet(new[] { entryValue! }, entryValue!.GetType()), childLabel, description, visited, false, property.Name + "_" + i.ToString(CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            ImGui.TextDisabled("<null>");
                        }

                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }

                ImGui.TreePop();
            }

            ImGui.PopID();
            return true;
        }

        private static Type? GetCollectionElementType(PropertyInfo property, Type runtimeType)
        {
            static Type? Resolve(Type type)
            {
                if (type.IsArray)
                    return type.GetElementType();

                if (type.IsGenericType)
                {
                    Type[] genericArguments = type.GetGenericArguments();
                    if (genericArguments.Length == 1)
                        return genericArguments[0];
                }

                foreach (var iface in type.GetInterfaces())
                {
                    if (!iface.IsGenericType)
                        continue;

                    Type genericDef = iface.GetGenericTypeDefinition();
                    if (genericDef == typeof(IList<>) || genericDef == typeof(IEnumerable<>) || genericDef == typeof(ICollection<>))
                        return iface.GetGenericArguments()[0];
                }

                return null;
            }

            return Resolve(property.PropertyType) ?? Resolve(runtimeType);
        }

        private static void DrawDictionarySimpleElement(IDictionary dictionary, object? key, Type elementType, ref object? currentValue, bool canModifyElements, object? owner)
        {
            Type effectiveType = Nullable.GetUnderlyingType(elementType) ?? elementType;
            bool isNullable = !elementType.IsValueType || Nullable.GetUnderlyingType(elementType) is not null;
            bool isCurrentlyNull = currentValue is null;
            bool handled = false;

            if (effectiveType == typeof(bool))
            {
                bool boolValue = currentValue is bool b && b;
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    if (ImGui.Checkbox("##Value", ref boolValue) && canModifyElements)
                    {
                        if (TryAssignDictionaryValue(dictionary, key, boolValue, owner))
                        {
                            currentValue = boolValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType.IsEnum)
            {
                string[] enumNames = Enum.GetNames(effectiveType);
                int currentIndex = currentValue is null ? -1 : Array.IndexOf(enumNames, Enum.GetName(effectiveType, currentValue));
                if (currentIndex < 0)
                    currentIndex = 0;

                int selectedIndex = currentIndex;
                using (new ImGuiDisabledScope(!canModifyElements || enumNames.Length == 0))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (enumNames.Length > 0 && ImGui.Combo("##Value", ref selectedIndex, enumNames, enumNames.Length) && canModifyElements && selectedIndex >= 0 && selectedIndex < enumNames.Length)
                    {
                        object newValue = Enum.Parse(effectiveType, enumNames[selectedIndex]);
                        if (TryAssignDictionaryValue(dictionary, key, newValue, owner))
                        {
                            currentValue = newValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(string))
            {
                string textValue = currentValue as string ?? string.Empty;
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputText("##Value", ref textValue, 512u) && canModifyElements)
                    {
                        if (TryAssignDictionaryValue(dictionary, key, textValue, owner))
                        {
                            currentValue = textValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(Vector2))
            {
                Vector2 vector = currentValue is Vector2 v ? v : Vector2.Zero;
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat2("##Value", ref vector, 0.05f) && canModifyElements)
                    {
                        if (TryAssignDictionaryValue(dictionary, key, vector, owner))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(Vector3))
            {
                Vector3 vector = currentValue is Vector3 v ? v : Vector3.Zero;
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat3("##Value", ref vector, 0.05f) && canModifyElements)
                    {
                        if (TryAssignDictionaryValue(dictionary, key, vector, owner))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(Vector4))
            {
                Vector4 vector = currentValue is Vector4 v ? v : Vector4.Zero;
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat4("##Value", ref vector, 0.05f) && canModifyElements)
                    {
                        if (TryAssignDictionaryValue(dictionary, key, vector, owner))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (TryDrawNumericEditor(effectiveType, canModifyElements, ref currentValue, ref isCurrentlyNull, newValue => TryAssignDictionaryValue(dictionary, key, newValue, owner), "##Value"))
            {
                handled = true;
            }

            if (!handled)
            {
                if (currentValue is null)
                    ImGui.TextDisabled("<null>");
                else
                    ImGui.TextUnformatted(FormatSettingValue(currentValue));
            }

            if (isNullable)
            {
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    if (isCurrentlyNull)
                    {
                        if (TryGetDefaultValue(effectiveType, out var defaultValue) && defaultValue is not null)
                        {
                            ImGui.SameLine();
                            if (ImGui.SmallButton("Set") && TryAssignDictionaryValue(dictionary, key, defaultValue, owner))
                            {
                                currentValue = defaultValue;
                                isCurrentlyNull = false;
                            }
                        }
                    }
                    else
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Clear") && TryAssignDictionaryValue(dictionary, key, null, owner))
                        {
                            currentValue = null;
                            isCurrentlyNull = true;
                        }
                    }
                }
            }
        }

        private static bool TryAssignDictionaryValue(IDictionary dictionary, object? key, object? newValue, object? owner)
        {
            try
            {
                if (!DictionaryContainsKey(dictionary, key))
                    return false;

                if (key is null)
                    return false;

                object? existing = dictionary[key];
                if (Equals(existing, newValue))
                    return false;

                dictionary[key] = newValue!;
                NotifyInspectorValueEdited(owner);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void DrawDictionaryAssetElement(PropertyInfo property, Type declaredValueType, Type? runtimeType, IDictionary dictionary, object? key, XRAsset? currentValue, object? owner)
        {
            var assetType = ResolveAssetEditorType(declaredValueType, runtimeType);
            if (assetType is null)
            {
                ImGui.TextDisabled("Asset editor unavailable for this type.");
                return;
            }

            string assetId = $"DictAsset_{property.Name}_{FormatDictionaryKey(key)}";
            if (!DrawAssetFieldForCollection(assetId, assetType, currentValue, selected =>
            {
                TryAssignDictionaryValue(dictionary, key, selected, owner);
            }))
            {
                ImGui.TextDisabled("Asset editor unavailable for this type.");
            }
        }

        private static bool TryAddDictionaryEntry(IDictionary dictionary, Type keyType, Type valueType, object? owner)
        {
            if (dictionary.IsReadOnly)
                return false;

            object? baseKey = CreateDefaultDictionaryKey(keyType);
            object? uniqueKey = EnsureUniqueDictionaryKey(dictionary, baseKey, keyType);
            if (uniqueKey is null)
            {
                Debug.LogWarning($"Unable to auto-generate a unique key for dictionary of '{keyType.Name}'.");
                return false;
            }

            object? newValue = ImGuiEditorUtilities.CreateDefaultElement(valueType);
            try
            {
                dictionary[uniqueKey] = newValue;
                NotifyInspectorValueEdited(owner);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to add dictionary entry '{keyType.Name}' → '{valueType.Name}'.");
                return false;
            }
        }

        private static object? CreateDefaultDictionaryKey(Type keyType)
        {
            keyType = Nullable.GetUnderlyingType(keyType) ?? keyType;

            if (keyType == typeof(string))
                return "Key";

            if (IsNumericType(keyType))
                return Convert.ChangeType(0, keyType, CultureInfo.InvariantCulture);

            if (keyType.IsValueType)
                return Activator.CreateInstance(keyType);

            if (keyType.GetConstructor(Type.EmptyTypes) is not null)
                return Activator.CreateInstance(keyType);

            return null;
        }

        private static object? EnsureUniqueDictionaryKey(IDictionary dictionary, object? baseKey, Type keyType)
        {
            if (baseKey is null)
                return null;

            if (!DictionaryContainsKey(dictionary, baseKey))
                return baseKey;

            if (keyType == typeof(string))
            {
                string root = string.IsNullOrWhiteSpace(baseKey as string) ? "Key" : (string)baseKey;
                int index = 1;
                string candidate;
                do
                {
                    candidate = $"{root}_{index++}";
                }
                while (DictionaryContainsKey(dictionary, candidate));
                return candidate;
            }

            if (IsNumericType(keyType))
            {
                long attempt = 0;
                if (baseKey is IConvertible convertible)
                    attempt = convertible.ToInt64(CultureInfo.InvariantCulture);

                object? candidate;
                do
                {
                    candidate = Convert.ChangeType(attempt++, keyType, CultureInfo.InvariantCulture);
                }
                while (candidate is not null && DictionaryContainsKey(dictionary, candidate));
                return candidate;
            }

            return null;
        }

        private static bool DictionaryContainsKey(IDictionary dictionary, object? key)
        {
            if (key is null)
            {
                foreach (var existing in dictionary.Keys)
                    if (existing is null)
                        return true;
                return false;
            }

            if (dictionary.Contains(key))
                return true;

            return FindDictionaryKeyByEquality(dictionary, key) is not null;
        }

        private static object? FindDictionaryKeyByEquality(IDictionary dictionary, object? key)
        {
            foreach (var existing in dictionary.Keys)
            {
                if (Equals(existing, key))
                    return existing;
            }
            return null;
        }

        private static bool TryRemoveDictionaryEntry(IDictionary dictionary, object? key)
        {
            if (dictionary.IsReadOnly)
                return false;

            try
            {
                if (key is null)
                {
                    object? match = FindDictionaryKeyByEquality(dictionary, null);
                    if (match is null)
                        return false;
                    dictionary.Remove(match);
                    return true;
                }

                if (dictionary.Contains(key))
                {
                    dictionary.Remove(key);
                    return true;
                }

                object? equalityMatch = FindDictionaryKeyByEquality(dictionary, key);
                if (equalityMatch is not null)
                {
                    dictionary.Remove(equalityMatch);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, "Failed to remove dictionary entry.");
            }

            return false;
        }

        private static bool TryReplaceDictionaryInstance(IDictionary dictionary, object? key, Type targetType, object? owner)
        {
            object? instance = CreateInstanceForCollectionType(targetType);
            if (instance is null)
                return false;

            if (!DictionaryContainsKey(dictionary, key))
                return false;

            if (key is null)
                return false;

            try
            {
                dictionary[key] = instance;
                NotifyInspectorValueEdited(owner);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to assign dictionary entry '{targetType.FullName}'.");
                return false;
            }
        }

        private static (Type KeyType, Type ValueType) ResolveDictionaryTypes(Type declaredType, Type runtimeType)
        {
            if (TryGetDictionaryArgs(declaredType, out var key, out var value))
                return (key, value);
            if (TryGetDictionaryArgs(runtimeType, out key, out value))
                return (key, value);
            return (typeof(object), typeof(object));
        }

        private static bool TryGetDictionaryArgs(Type type, out Type key, out Type value)
        {
            if (type.IsGenericType)
            {
                var def = type.GetGenericTypeDefinition();
                if (def == typeof(IDictionary<,>) || def == typeof(Dictionary<,>))
                {
                    var args = type.GetGenericArguments();
                    key = args[0];
                    value = args[1];
                    return true;
                }
            }

            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                {
                    var args = iface.GetGenericArguments();
                    key = args[0];
                    value = args[1];
                    return true;
                }
            }

            key = typeof(object);
            value = typeof(object);
            return false;
        }

        private static string FormatDictionaryKey(object? key)
            => key?.ToString() ?? "<null>";

        private static bool TryCreateLinkedListAdapter(object value, out IList adapter)
        {
            adapter = null!;
            Type type = value.GetType();
            if (!IsLinkedListType(type))
                return false;

            Type elementType = type.GetGenericArguments()[0];
            Type adapterType = typeof(LinkedListAdapter<>).MakeGenericType(elementType);
            adapter = (IList)Activator.CreateInstance(adapterType, value)!;
            return true;
        }

        private static bool TryCreateCollectionBufferAdapter(object value, Type elementType, out IList adapter)
        {
            adapter = null!;
            if (value is not ICollection collection || value is IList || value is IDictionary)
                return false;

            adapter = new CollectionBufferAdapter(collection, elementType);
            return true;
        }

        private static bool IsLinkedListType(Type type)
        {
            while (type is not null && type != typeof(object))
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(LinkedList<>))
                    return true;
                type = type.BaseType!;
            }
            return false;
        }

        private sealed class LinkedListAdapter<T>(LinkedList<T> list) : IList
        {
            private readonly LinkedList<T> _list = list ?? throw new ArgumentNullException(nameof(list));

            public object? this[int index]
            {
                get => (GetNode(index) ?? throw new ArgumentOutOfRangeException(nameof(index))).Value;
                set
                {
                    var node = GetNode(index) ?? throw new ArgumentOutOfRangeException(nameof(index));
                    node.Value = value is T typed ? typed : (T?)ConvertCollectionElement(value, typeof(T)) ?? default!;
                }
            }

            public bool IsReadOnly => false;
            public bool IsFixedSize => false;
            public int Count => _list.Count;
            public object SyncRoot => this;
            public bool IsSynchronized => false;

            public int Add(object? value)
            {
                _list.AddLast(value is T typed ? typed : (T?)ConvertCollectionElement(value, typeof(T)) ?? default!);
                return _list.Count - 1;
            }

            public void Clear() => _list.Clear();

            public bool Contains(object? value)
            {
                var converted = value is T typed ? typed : (T?)ConvertCollectionElement(value, typeof(T));
                if (converted is null)
                    return false;
                return _list.Contains(converted);
            }

            public int IndexOf(object? value)
            {
                int index = 0;
                foreach (var entry in _list)
                {
                    if (Equals(entry, value))
                        return index;
                    index++;
                }
                return -1;
            }

            public void Insert(int index, object? value)
            {
                if (index < 0 || index > _list.Count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                if (index == _list.Count)
                {
                    Add(value);
                    return;
                }

                var node = GetNode(index) ?? throw new ArgumentOutOfRangeException(nameof(index));
                _list.AddBefore(node, value is T typed ? typed : (T?)ConvertCollectionElement(value, typeof(T)) ?? default!);
            }

            public void Remove(object? value)
            {
                if (value is T typed)
                    _list.Remove(typed);
                else
                {
                    var node = _list.First;
                    while (node is not null)
                    {
                        if (Equals(node.Value, value))
                        {
                            _list.Remove(node);
                            break;
                        }
                        node = node.Next;
                    }
                }
            }

            public void RemoveAt(int index)
            {
                var node = GetNode(index) ?? throw new ArgumentOutOfRangeException(nameof(index));
                _list.Remove(node);
            }

            public void CopyTo(Array array, int index)
            {
                foreach (var entry in _list)
                    array.SetValue(entry, index++);
            }

            public IEnumerator GetEnumerator() => _list.GetEnumerator();

            private LinkedListNode<T>? GetNode(int index)
            {
                if (index < 0 || index >= _list.Count)
                    return null;

                var node = _list.First;
                for (int i = 0; i < index && node is not null; i++)
                    node = node.Next;
                return node;
            }
        }

        private sealed class CollectionBufferAdapter : IList
        {
            private readonly ICollection _collection;
            private readonly CollectionAccessor? _accessor;
            private readonly Type _elementType;
            private readonly List<object?> _buffer;

            public CollectionBufferAdapter(ICollection collection, Type elementType)
            {
                _collection = collection;
                _elementType = elementType;
                _buffer = new List<object?>();
                foreach (var item in collection)
                    _buffer.Add(item);
                _accessor = CollectionAccessor.Create(collection.GetType());
            }

            private bool CanMutate => _accessor is not null;

            public object? this[int index]
            {
                get => _buffer[index];
                set
                {
                    _buffer[index] = value;
                    Apply();
                }
            }

            public bool IsReadOnly => !CanMutate;
            public bool IsFixedSize => false;
            public int Count => _buffer.Count;
            public object SyncRoot => this;
            public bool IsSynchronized => false;

            public int Add(object? value)
            {
                _buffer.Add(value);
                Apply();
                return _buffer.Count - 1;
            }

            public void Clear()
            {
                _buffer.Clear();
                Apply();
            }

            public bool Contains(object? value) => _buffer.Contains(value);

            public int IndexOf(object? value) => _buffer.IndexOf(value);

            public void Insert(int index, object? value)
            {
                _buffer.Insert(index, value);
                Apply();
            }

            public void Remove(object? value)
            {
                if (_buffer.Remove(value))
                    Apply();
            }

            public void RemoveAt(int index)
            {
                _buffer.RemoveAt(index);
                Apply();
            }

            public void CopyTo(Array array, int index)
            {
                foreach (var entry in _buffer)
                    array.SetValue(entry, index++);
            }

            public IEnumerator GetEnumerator() => _buffer.GetEnumerator();

            private void Apply()
            {
                if (!CanMutate)
                    return;
                _accessor!.Apply(_collection, _buffer, _elementType);
            }
        }

        private sealed class CollectionAccessor
        {
            private readonly MethodInfo? _clear;
            private readonly MethodInfo? _add;
            private readonly Type? _addParameter;

            private CollectionAccessor(MethodInfo? clear, MethodInfo? add)
            {
                _clear = clear;
                _add = add;
                _addParameter = add?.GetParameters().FirstOrDefault()?.ParameterType;
            }

            public bool CanMutate => _clear is not null && _add is not null;

            public void Apply(object collection, IEnumerable<object?> buffer, Type fallbackType)
            {
                if (!CanMutate)
                    return;

                _clear!.Invoke(collection, null);
                foreach (var entry in buffer)
                    _add!.Invoke(collection, new[] { ConvertCollectionElement(entry, _addParameter ?? fallbackType) });
            }

            public static CollectionAccessor? Create(Type type)
            {
                MethodInfo? clear = FindCollectionMethod(type, "Clear", 0);
                MethodInfo? add = FindCollectionMethod(type, "Add", 1);

                if (clear is null || add is null)
                    return null;

                return new CollectionAccessor(clear, add);
            }
        }

        private static MethodInfo? FindCollectionMethod(Type type, string name, int parameterCount)
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var method = type.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameterCount);
            if (method is not null)
                return method;

            foreach (var iface in type.GetInterfaces())
            {
                var map = type.GetInterfaceMap(iface);
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    var ifaceMethod = map.InterfaceMethods[i];
                    if (ifaceMethod.Name != name)
                        continue;
                    if (ifaceMethod.GetParameters().Length != parameterCount)
                        continue;
                    return map.TargetMethods[i];
                }
            }

            return null;
        }

        private static object? ConvertCollectionElement(object? value, Type targetType)
        {
            targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (value is null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            if (targetType.IsInstanceOfType(value))
                return value;

            try
            {
                if (targetType.IsEnum)
                {
                    if (value is string s && Enum.IsDefined(targetType, s))
                        return Enum.Parse(targetType, s, true);
                    return Enum.ToObject(targetType, value);
                }

                return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
            }
            catch
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }
        }

        private static bool IsNumericType(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type == typeof(byte)
                || type == typeof(sbyte)
                || type == typeof(short)
                || type == typeof(ushort)
                || type == typeof(int)
                || type == typeof(uint)
                || type == typeof(long)
                || type == typeof(ulong)
                || type == typeof(float)
                || type == typeof(double)
                || type == typeof(decimal);
        }

        private static bool ShouldUseCollectionTypeSelector(Type type)
        {
            Type effective = Nullable.GetUnderlyingType(type) ?? type;
            if (typeof(XRAsset).IsAssignableFrom(effective))
                return false;
            if (IsSimpleSettingType(effective))
                return false;
            return !effective.IsValueType;
        }

        /// <summary>
        /// Determines whether a property type should show a type selector for creating new instances.
        /// </summary>
        private static bool ShouldUsePropertyTypeSelector(Type propertyType)
        {
            Type effective = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (typeof(XRAsset).IsAssignableFrom(effective))
                return true; // Assets should use asset picker
            if (IsSimpleSettingType(effective))
                return false;
            // Reference types and interfaces can have derived types
            return effective.IsClass || effective.IsInterface;
        }

        /// <summary>
        /// Gets the available concrete types that can be instantiated for a property type.
        /// </summary>
        private static IReadOnlyList<CollectionTypeDescriptor> GetPropertyTypeDescriptors(Type baseType)
        {
            baseType = Nullable.GetUnderlyingType(baseType) ?? baseType;

            if (_propertyTypeDescriptorCache.TryGetValue(baseType, out var cached))
                return cached;

            var descriptors = new List<CollectionTypeDescriptor>();

            // If the base type itself is concrete with a parameterless constructor, include it
            if (!baseType.IsAbstract && !baseType.IsInterface && !baseType.ContainsGenericParameters
                && baseType.GetConstructor(Type.EmptyTypes) is not null)
            {
                descriptors.Add(new CollectionTypeDescriptor(
                    baseType,
                    baseType.Name,
                    baseType.Namespace ?? string.Empty,
                    baseType.Assembly.GetName().Name ?? baseType.Assembly.FullName ?? "Unknown"));
            }

            // Search all loaded assemblies for derived types
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
                    if (type == baseType)
                        continue; // Already added above if applicable
                    if (!baseType.IsAssignableFrom(type))
                        continue;
                    if (type.IsAbstract || type.IsInterface)
                        continue;
                    if (type.ContainsGenericParameters)
                        continue;
                    if (type.GetConstructor(Type.EmptyTypes) is null)
                        continue;

                    descriptors.Add(new CollectionTypeDescriptor(
                        type,
                        type.Name,
                        type.Namespace ?? string.Empty,
                        assembly.GetName().Name ?? assembly.FullName ?? "Unknown"));
                }
            }

            descriptors.Sort(static (a, b) =>
            {
                int nameCompare = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                if (nameCompare != 0)
                    return nameCompare;
                int nsCompare = string.Compare(a.Namespace, b.Namespace, StringComparison.OrdinalIgnoreCase);
                if (nsCompare != 0)
                    return nsCompare;
                return string.Compare(a.AssemblyName, b.AssemblyName, StringComparison.OrdinalIgnoreCase);
            });

            _propertyTypeDescriptorCache[baseType] = descriptors;
            return descriptors;
        }

        /// <summary>
        /// Draws a popup for selecting a concrete type to instantiate for a property.
        /// </summary>
        private static void DrawPropertyTypePickerPopup(string popupId, Type baseType, Action<Type> onSelected)
        {
            if (!ImGui.BeginPopup(popupId))
                return;

            string searchKey = popupId;
            string search = _propertyTypePickerSearch.TryGetValue(searchKey, out var existing) ? existing : string.Empty;
            if (ImGui.InputTextWithHint("##PropertyTypeSearch", "Search types...", ref search, 256u))
                _propertyTypePickerSearch[searchKey] = search.Trim();

            ImGui.Separator();

            var descriptors = GetPropertyTypeDescriptors(baseType);
            IEnumerable<CollectionTypeDescriptor> filtered = descriptors;
            if (!string.IsNullOrWhiteSpace(search))
            {
                filtered = descriptors.Where(d =>
                    d.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(d.Namespace) && d.Namespace.Contains(search, StringComparison.OrdinalIgnoreCase))
                    || d.FullName.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            var filteredList = filtered.ToList();

            if (ImGui.BeginChild("##PropertyTypeList", new Vector2(0f, 280f), ImGuiChildFlags.Border))
            {
                if (filteredList.Count == 0)
                {
                    ImGui.TextDisabled("No matching types found.");
                }
                else
                {
                    foreach (var descriptor in filteredList)
                    {
                        string label = $"{descriptor.DisplayName}##{descriptor.FullName}";
                        if (ImGui.Selectable(label, false))
                        {
                            onSelected(descriptor.Type);
                            ImGui.CloseCurrentPopup();
                            ImGui.EndChild();
                            ImGui.EndPopup();
                            return;
                        }

                        if (ImGui.IsItemHovered())
                        {
                            string tooltip = descriptor.FullName;
                            if (!string.IsNullOrEmpty(descriptor.AssemblyName))
                                tooltip += $" ({descriptor.AssemblyName})";
                            ImGui.SetTooltip(tooltip);
                        }
                    }
                }

                ImGui.EndChild();
            }

            if (ImGui.Button("Close"))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }

        /// <summary>
        /// Draws UI for a complex property that is currently null, allowing the user to create a new instance.
        /// </summary>
        private static void DrawNullComplexProperty(object owner, PropertyInfo property, string displayName, string? description)
        {
            Type propertyType = property.PropertyType;
            Type effectiveType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            bool canWrite = property.CanWrite && property.SetMethod?.IsPublic == true;
            bool isNullable = Nullable.GetUnderlyingType(propertyType) is not null || !propertyType.IsValueType;

            ImGui.PushID(property.Name);

            // Check if this is an asset type
            bool isAssetType = typeof(XRAsset).IsAssignableFrom(effectiveType);
            if (isAssetType)
            {
                ImGui.TextUnformatted($"{displayName}:");
                if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                    ImGui.SetTooltip(description);

                ImGui.SameLine();

                // Draw asset field for null asset (use declared type so derived types can be created/selected)
                if (!DrawAssetFieldForProperty(property.Name, effectiveType, null, owner, property))
                {
                    ImGui.TextDisabled("<null>");
                }
            }
            else if (ShouldUsePropertyTypeSelector(effectiveType))
            {
                var typeDescriptors = GetPropertyTypeDescriptors(effectiveType);
                int typeCount = typeDescriptors.Count;

                ImGui.TextUnformatted($"{displayName}:");
                if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                    ImGui.SetTooltip(description);

                ImGui.SameLine();
                ImGui.TextDisabled("<null>");

                if (canWrite && isNullable && typeCount > 0)
                {
                    ImGui.SameLine();

                    string createPopupId = $"CreateProperty_{property.Name}";

                    if (typeCount == 1)
                    {
                        // Single type available - create directly
                        string buttonLabel = $"Create {typeDescriptors[0].DisplayName}";
                        if (ImGui.SmallButton(buttonLabel))
                        {
                            TryCreateAndSetPropertyValue(owner, property, typeDescriptors[0].Type);
                        }
                    }
                    else
                    {
                        // Multiple types available - show picker
                        if (ImGui.SmallButton("Create..."))
                            ImGui.OpenPopup(createPopupId);

                        DrawPropertyTypePickerPopup(createPopupId, effectiveType, selectedType =>
                        {
                            TryCreateAndSetPropertyValue(owner, property, selectedType);
                        });
                    }
                }
                else if (!canWrite)
                {
                    // Property is read-only, just show null
                }
                else if (typeCount == 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("(no concrete types available)");
                }
            }
            else
            {
                // Not a type that uses type selector, just show null
                ImGui.TextUnformatted($"{displayName}: <null>");
                if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                    ImGui.SetTooltip(description);
            }

            ImGui.PopID();
        }

        /// <summary>
        /// Attempts to create a new instance of the specified type and set it on the property.
        /// </summary>
        private static bool TryCreateAndSetPropertyValue(object owner, PropertyInfo property, Type instanceType)
        {
            try
            {
                object? instance = Activator.CreateInstance(instanceType);
                if (instance is null)
                {
                    Debug.LogWarning($"Failed to create instance of '{instanceType.FullName}' for property '{property.Name}'.");
                    return false;
                }

                property.SetValue(owner, instance);
                NotifyInspectorValueEdited(owner);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to create and set instance of '{instanceType.FullName}' for property '{property.Name}'.");
                return false;
            }
        }

        /// <summary>
        /// Draws an asset picker field for a property, allowing the user to select an asset.
        /// </summary>
        private static bool DrawAssetFieldForProperty(string id, Type assetType, XRAsset? currentValue, object owner, PropertyInfo property)
        {
            bool allowClear = IsPropertyNullable(property);
            bool allowCreateReplace = property.CanWrite && property.SetMethod?.IsPublic == true;

            if (!DrawAssetFieldForCollection(id, assetType, currentValue, selected =>
            {
                try
                {
                    property.SetValue(owner, selected);
                    NotifyInspectorValueEdited(owner);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, $"Failed to set asset value for property '{property.Name}'.");
                }
            }, allowClear: allowClear, allowCreateOrReplace: allowCreateReplace))
            {
                return false;
            }
            return true;
        }

        private static string GetFriendlyCollectionTypeName(Type type)
        {
            Type effective = Nullable.GetUnderlyingType(type) ?? type;
            string name = effective.Name;
            int backtick = name.IndexOf('`');
            if (backtick >= 0)
                name = name[..backtick];
            return string.IsNullOrWhiteSpace(name) ? effective.FullName ?? effective.Name : name;
        }

        private static void DrawCollectionTypePickerPopup(string popupId, Type baseType, Type? currentType, Action<Type> onSelected)
        {
            if (!ImGui.BeginPopup(popupId))
                return;

            string searchKey = popupId;
            string search = _collectionTypePickerSearch.TryGetValue(searchKey, out var existing) ? existing : string.Empty;
            if (ImGui.InputTextWithHint("##CollectionTypeSearch", "Search...", ref search, 256u))
                _collectionTypePickerSearch[searchKey] = search.Trim();

            ImGui.Separator();

            var descriptors = GetCollectionTypeDescriptors(baseType);
            IEnumerable<CollectionTypeDescriptor> filtered = descriptors;
            if (!string.IsNullOrWhiteSpace(search))
            {
                filtered = descriptors.Where(d =>
                    d.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(d.Namespace) && d.Namespace.Contains(search, StringComparison.OrdinalIgnoreCase))
                    || d.FullName.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            var filteredList = filtered.ToList();

            if (ImGui.BeginChild("##CollectionTypeList", new Vector2(0f, 240f), ImGuiChildFlags.Border))
            {
                if (filteredList.Count == 0)
                {
                    ImGui.TextDisabled("No matching types.");
                }
                else
                {
                    foreach (var descriptor in filteredList)
                    {
                        bool selected = currentType == descriptor.Type;
                        string label = $"{descriptor.DisplayName}##{descriptor.FullName}";
                        if (ImGui.Selectable(label, selected))
                        {
                            onSelected(descriptor.Type);
                            ImGui.CloseCurrentPopup();
                            ImGui.EndChild();
                            ImGui.EndPopup();
                            return;
                        }

                        if (ImGui.IsItemHovered())
                        {
                            string tooltip = descriptor.FullName;
                            if (!string.IsNullOrEmpty(descriptor.AssemblyName))
                                tooltip += $" ({descriptor.AssemblyName})";
                            ImGui.SetTooltip(tooltip);
                        }

                        if (selected)
                            ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndChild();
            }

            if (ImGui.Button("Close"))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }

        private static IReadOnlyList<CollectionTypeDescriptor> GetCollectionTypeDescriptors(Type baseType)
        {
            baseType = Nullable.GetUnderlyingType(baseType) ?? baseType;

            if (_collectionTypeDescriptorCache.TryGetValue(baseType, out var cached))
                return cached;

            var descriptors = new List<CollectionTypeDescriptor>();
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
                    if (type.GetConstructor(Type.EmptyTypes) is null)
                        continue;

                    descriptors.Add(new CollectionTypeDescriptor(
                        type,
                        type.Name,
                        type.Namespace ?? string.Empty,
                        assembly.GetName().Name ?? assembly.FullName ?? "Unknown"));
                }
            }

            descriptors.Sort(static (a, b) =>
            {
                int nameCompare = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                if (nameCompare != 0)
                    return nameCompare;
                int nsCompare = string.Compare(a.Namespace, b.Namespace, StringComparison.OrdinalIgnoreCase);
                if (nsCompare != 0)
                    return nsCompare;
                return string.Compare(a.AssemblyName, b.AssemblyName, StringComparison.OrdinalIgnoreCase);
            });

            _collectionTypeDescriptorCache[baseType] = descriptors;
            return descriptors;
        }

        private static object? CreateInstanceForCollectionType(Type type)
        {
            try
            {
                return Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to create instance of '{type.FullName}': {ex.Message}");
                return null;
            }
        }

        private static void DrawCollectionAssetElement(Type declaredElementType, Type? runtimeType, XRAsset? currentValue, ImGuiEditorUtilities.CollectionEditorAdapter adapter, int index, object? owner)
        {
            Type? assetType = ResolveAssetEditorType(declaredElementType, runtimeType);
            if (assetType is null)
            {
                ImGui.TextDisabled("Asset editor unavailable for this type.");
                return;
            }

            if (!DrawAssetFieldForCollection("AssetValue", assetType, currentValue, selected =>
            {
                if (adapter.TryReplace(index, selected))
                    NotifyInspectorValueEdited(owner);
            }))
            {
                ImGui.TextDisabled("Asset editor unavailable for this type.");
            }
        }

        private static void DrawCollectionAssetAddPopup(string popupId, ImGuiEditorUtilities.CollectionEditorAdapter adapter, object? owner, IReadOnlyList<CollectionTypeDescriptor> assetTypeOptions)
        {
            if (!ImGui.BeginPopup(popupId))
                return;

            if (assetTypeOptions.Count == 0)
            {
                ImGui.TextDisabled("No concrete asset types available.");
                if (ImGui.Button("Close"))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
                return;
            }

            bool closeRequested = false;
            for (int i = 0; i < assetTypeOptions.Count; i++)
            {
                var descriptor = assetTypeOptions[i];
                ImGui.PushID(descriptor.FullName);
                ImGui.TextUnformatted(descriptor.DisplayName);

                if (!DrawAssetFieldForCollection("NewAssetValue", descriptor.Type, null, selected =>
                {
                    if (selected is null)
                        return;
                    if (adapter.TryAdd(selected))
                    {
                        NotifyInspectorValueEdited(owner);
                        closeRequested = true;
                    }
                }))
                {
                    ImGui.TextDisabled("Unable to draw asset selector for this type.");
                }

                ImGui.PopID();

                if (i < assetTypeOptions.Count - 1)
                    ImGui.Separator();
            }

            if (closeRequested)
            {
                ImGui.CloseCurrentPopup();
            }
            else if (ImGui.Button("Close"))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        private static bool TryAddCollectionInstance(ImGuiEditorUtilities.CollectionEditorAdapter adapter, Type type, object? owner)
        {
            object? instance = CreateInstanceForCollectionType(type);
            if (instance is null)
                return false;
            if (!adapter.TryAdd(instance))
                return false;

            NotifyInspectorValueEdited(owner);
            return true;
        }

        private static bool TryReplaceCollectionInstance(ImGuiEditorUtilities.CollectionEditorAdapter adapter, int index, Type type, object? owner)
        {
            object? instance = CreateInstanceForCollectionType(type);
            if (instance is null)
                return false;
            if (!adapter.TryReplace(index, instance))
                return false;

            NotifyInspectorValueEdited(owner);
            return true;
        }

        private static bool DrawAssetFieldForCollection(
            string id,
            Type assetType,
            XRAsset? current,
            Action<XRAsset?> assign,
            bool allowClear = true,
            bool allowCreateOrReplace = false)
        {
            if (!typeof(XRAsset).IsAssignableFrom(assetType))
                return false;
            if (assetType.ContainsGenericParameters)
                return false;

            _drawAssetCollectionElementMethod.MakeGenericMethod(assetType)
                .Invoke(null, new object?[] { id, current, assign, allowClear, allowCreateOrReplace });
            return true;
        }

        private static Type? ResolveAssetEditorType(Type declaredElementType, Type? runtimeType)
        {
            Type? candidate = runtimeType ?? (Nullable.GetUnderlyingType(declaredElementType) ?? declaredElementType);
            if (candidate is null || !typeof(XRAsset).IsAssignableFrom(candidate))
                return null;

            if (!candidate.IsAbstract && !candidate.ContainsGenericParameters && candidate.GetConstructor(Type.EmptyTypes) is not null)
                return candidate;

            return null;
        }

        private static void DrawAssetCollectionElementGeneric<TAsset>(
            string id,
            XRAsset? currentBase,
            Action<XRAsset?> assign,
            bool allowClear,
            bool allowCreateOrReplace)
            where TAsset : XRAsset
        {
            TAsset? typedCurrent = currentBase as TAsset;
            ImGuiAssetUtilities.DrawAssetField<TAsset>(id, typedCurrent, asset => assign(asset), options: null, allowClear: allowClear, allowCreateOrReplace: allowCreateOrReplace);
        }

        private static void DrawCollectionSimpleElement(IList list, Type elementType, int index, ref object? currentValue, bool canModifyElements)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawCollectionSimpleElement");
            Type effectiveType = Nullable.GetUnderlyingType(elementType) ?? elementType;
            bool isNullable = !elementType.IsValueType || Nullable.GetUnderlyingType(elementType) is not null;
            bool isCurrentlyNull = currentValue is null;
            bool handled = false;

            if (effectiveType == typeof(bool))
            {
                bool boolValue = currentValue is bool b && b;
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    if (ImGui.Checkbox("##Value", ref boolValue) && canModifyElements)
                    {
                        if (TryAssignCollectionValue(list, index, boolValue))
                        {
                            currentValue = boolValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType.IsEnum)
            {
                string[] enumNames = Enum.GetNames(effectiveType);
                int currentIndex = currentValue is null ? -1 : Array.IndexOf(enumNames, Enum.GetName(effectiveType, currentValue));
                if (currentIndex < 0)
                    currentIndex = 0;

                int selectedIndex = currentIndex;
                using (new ImGuiDisabledScope(!canModifyElements || enumNames.Length == 0))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (enumNames.Length > 0 && ImGui.Combo("##Value", ref selectedIndex, enumNames, enumNames.Length) && canModifyElements && selectedIndex >= 0 && selectedIndex < enumNames.Length)
                    {
                        object newValue = Enum.Parse(effectiveType, enumNames[selectedIndex]);
                        if (TryAssignCollectionValue(list, index, newValue))
                        {
                            currentValue = newValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(string))
            {
                string textValue = currentValue as string ?? string.Empty;
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputText("##Value", ref textValue, 512u, ImGuiInputTextFlags.None) && canModifyElements)
                    {
                        if (TryAssignCollectionValue(list, index, textValue))
                        {
                            currentValue = textValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(Vector2))
            {
                Vector2 vector = currentValue is Vector2 v ? v : Vector2.Zero;
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat2("##Value", ref vector, 0.05f) && canModifyElements)
                    {
                        if (TryAssignCollectionValue(list, index, vector))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(Vector3))
            {
                Vector3 vector = currentValue is Vector3 v ? v : Vector3.Zero;
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat3("##Value", ref vector, 0.05f) && canModifyElements)
                    {
                        if (TryAssignCollectionValue(list, index, vector))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(Vector4))
            {
                Vector4 vector = currentValue is Vector4 v ? v : Vector4.Zero;
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat4("##Value", ref vector, 0.05f) && canModifyElements)
                    {
                        if (TryAssignCollectionValue(list, index, vector))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }

            else if (TryDrawNumericCollectionElement(effectiveType, canModifyElements, ref currentValue, ref isCurrentlyNull, list, index))
            {
                handled = true;
            }

            if (!handled)
            {
                if (currentValue is null)
                    ImGui.TextDisabled("<null>");
                else
                    ImGui.TextUnformatted(FormatSettingValue(currentValue));
            }

            if (isNullable)
            {
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    if (isCurrentlyNull)
                    {
                        if (TryGetDefaultValue(effectiveType, out var defaultValue) && defaultValue is not null)
                        {
                            ImGui.SameLine();
                            if (ImGui.SmallButton("Set"))
                            {
                                if (TryAssignCollectionValue(list, index, defaultValue))
                                {
                                    currentValue = defaultValue;
                                    isCurrentlyNull = false;
                                }
                            }
                        }
                    }
                    else
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Clear"))
                        {
                            if (TryAssignCollectionValue(list, index, null))
                            {
                                currentValue = null;
                                isCurrentlyNull = true;
                            }
                        }
                    }
                }
            }
        }

        private static bool TryDrawNumericCollectionElement(Type effectiveType, bool canWrite, ref object? currentValue, ref bool isCurrentlyNull, IList list, int index)
            => TryDrawNumericEditor(effectiveType, canWrite, ref currentValue, ref isCurrentlyNull, newValue => TryAssignCollectionValue(list, index, newValue), "##Value");

        private static bool TryAssignCollectionValue(IList list, int index, object? newValue)
        {
            try
            {
                object? existing = list[index];
                if (Equals(existing, newValue))
                    return false;

                list[index] = newValue!;
                NotifyInspectorValueEdited(null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryApplyInspectorValue(InspectorTargetSet targets, PropertyInfo property, IReadOnlyList<object?> previousValues, object? newValue)
        {
            bool changed = false;
            int count = Math.Min(previousValues.Count, targets.Targets.Count);

            for (int i = 0; i < targets.Targets.Count; i++)
            {
                object target = targets.Targets[i];
                object? previousValue = i < count ? previousValues[i] : null;

                if (Equals(previousValue, newValue))
                    continue;

                try
                {
                    property.SetValue(target, newValue);
                    NotifyInspectorValueEdited(target);
                    changed = true;
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, $"Failed to set value for property '{property.Name}' on '{target.GetType().Name}'.");
                }
            }

            return changed;
        }

        private static bool TryApplyInspectorValue(object owner, PropertyInfo property, object? previousValue, object? newValue)
        {
            if (Equals(previousValue, newValue))
                return false;

            property.SetValue(owner, newValue);
            NotifyInspectorValueEdited(owner);
            return true;
        }

        private static bool TryApplyInspectorValue(InspectorTargetSet targets, FieldInfo field, IReadOnlyList<object?> previousValues, object? newValue)
        {
            bool changed = false;
            int count = Math.Min(previousValues.Count, targets.Targets.Count);

            for (int i = 0; i < targets.Targets.Count; i++)
            {
                object target = targets.Targets[i];
                object? previousValue = i < count ? previousValues[i] : null;

                if (Equals(previousValue, newValue))
                    continue;

                try
                {
                    field.SetValue(target, newValue);
                    NotifyInspectorValueEdited(target);
                    changed = true;
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, $"Failed to set value for field '{field.Name}' on '{target.GetType().Name}'.");
                }
            }

            return changed;
        }

        private static void NotifyInspectorValueEdited(object? valueOwner)
        {
            XRAsset? asset = null;

            if (valueOwner is XRAsset assetOwner)
                asset = assetOwner.SourceAsset;
            else if (_inspectorAssetContext is not null)
                asset = _inspectorAssetContext;

            if (asset is not null && !asset.IsDirty)
                asset.MarkDirty();
        }

        private static void NotifyInspectorValueEdited(InspectorTargetSet targets)
        {
            foreach (var target in targets.Targets)
                NotifyInspectorValueEdited(target);
        }

        private static void DrawSimplePropertyRow(InspectorTargetSet targets, PropertyInfo property, IReadOnlyList<object?> values, string displayName, string? description, bool valueRetrievalFailed)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSimplePropertyRow");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(displayName);
            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);
            ImGui.TableSetColumnIndex(1);
            ImGui.PushID(property.Name);

            if (valueRetrievalFailed)
            {
                ImGui.TextDisabled("<error>");
                ImGui.PopID();
                return;
            }

            if (!targets.HasMultipleTargets && typeof(IOverrideableSetting).IsAssignableFrom(property.PropertyType) && values.FirstOrDefault() is IOverrideableSetting overrideable)
            {
                var descriptor = new SettingPropertyDescriptor
                {
                    Property = property,
                    Values = values,
                    ValueRetrievalFailed = false,
                    IsSimple = true,
                    Category = null,
                    DisplayName = displayName,
                    Description = description,
                    IsOverrideable = true
                };

                DrawOverrideableSettingRow(targets.PrimaryTarget, descriptor, overrideable);
                ImGui.PopID();
                return;
            }

            Type propertyType = property.PropertyType;
            Type? underlyingType = Nullable.GetUnderlyingType(propertyType);
            bool isNullable = underlyingType is not null;
            Type effectiveType = underlyingType ?? propertyType;
            bool canWrite = property.CanWrite && property.SetMethod?.IsPublic == true;

            object? firstValue = values.Count > 0 ? values[0] : null;
            bool hasMixedValues = values.Skip(1).Any(v => !Equals(v, firstValue));

            // Asset reference fields: draw as an asset picker with inline inspector.
            if (typeof(XRAsset).IsAssignableFrom(effectiveType))
            {
                XRAsset? currentAsset = firstValue as XRAsset;
                Type declaredAssetType = effectiveType;

                bool allowClear = canWrite && IsPropertyNullable(property);
                bool allowCreateReplace = canWrite;

                if (!DrawAssetFieldForCollection(property.Name, declaredAssetType, currentAsset, selected =>
                {
                    try
                    {
                        foreach (var target in targets.Targets)
                            property.SetValue(target, selected);
                        NotifyInspectorValueEdited(targets);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, $"Failed to set asset value for property '{property.Name}'.");
                    }
                }, allowClear: allowClear, allowCreateOrReplace: allowCreateReplace))
                    ImGui.TextDisabled("Asset editor unavailable for this type.");

                ImGui.PopID();
                return;
            }

            object? currentValue = firstValue;
            bool isCurrentlyNull = currentValue is null;
            bool handled = false;

            if (effectiveType == typeof(bool))
            {
                bool boolValue = currentValue is bool b && b;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    if (hasMixedValues)
                        ImGui.SetItemDefaultFocus();
                    if (ImGui.Checkbox("##Value", ref boolValue) && canWrite)
                    {
                        if (TryApplyInspectorValue(targets, property, values, boolValue))
                        {
                            currentValue = boolValue;
                            isCurrentlyNull = false;
                            hasMixedValues = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType.IsEnum)
            {
                if (effectiveType.IsDefined(typeof(FlagsAttribute), inherit: false))
                {
                    ulong bits = 0;
                    try
                    {
                        if (currentValue is not null)
                            bits = Convert.ToUInt64(currentValue, CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        bits = 0;
                    }

                    Array enumValues = Enum.GetValues(effectiveType);
                    string preview = hasMixedValues ? "<multiple>" : FormatFlagsEnumPreview(effectiveType, enumValues, bits);

                    using (new ImGuiDisabledScope(!canWrite))
                    {
                        ImGui.SetNextItemWidth(-1f);
                        ImGui.SetNextWindowSizeConstraints(new Vector2(420.0f, 0.0f), new Vector2(float.MaxValue, float.MaxValue));
                        if (ImGui.BeginCombo("##Value", preview, ImGuiComboFlags.HeightLarge))
                        {
                            if (ImGui.SmallButton("Clear"))
                            {
                                ulong newBits = 0;
                                object newValue = Enum.ToObject(effectiveType, newBits);
                                if (TryApplyInspectorValue(targets, property, values, newValue))
                                {
                                    currentValue = newValue;
                                    isCurrentlyNull = false;
                                    bits = newBits;
                                    hasMixedValues = false;
                                }
                            }

                            ImGui.Separator();

                            for (int i = 0; i < enumValues.Length; i++)
                            {
                                object raw = enumValues.GetValue(i)!;
                                ulong flag;
                                try
                                {
                                    flag = Convert.ToUInt64(raw, CultureInfo.InvariantCulture);
                                }
                                catch
                                {
                                    continue;
                                }

                                if (flag == 0)
                                    continue;

                                bool isSet = (bits & flag) == flag;
                                string name = Enum.GetName(effectiveType, raw) ?? raw.ToString() ?? $"0x{flag:X}";

                                bool checkedNow = isSet;
                                if (ImGui.Checkbox(name, ref checkedNow))
                                {
                                    ulong newBits = checkedNow ? (bits | flag) : (bits & ~flag);
                                    object newValue = Enum.ToObject(effectiveType, newBits);
                                    if (TryApplyInspectorValue(targets, property, values, newValue))
                                    {
                                        currentValue = newValue;
                                        isCurrentlyNull = false;
                                        bits = newBits;
                                        hasMixedValues = false;
                                    }
                                }
                            }

                            ImGui.EndCombo();
                        }
                    }

                    handled = true;
                }
                else
                {
                    string[] enumNames = Enum.GetNames(effectiveType);
                    int currentIndex = currentValue is null ? -1 : Array.IndexOf(enumNames, Enum.GetName(effectiveType, currentValue));
                    if (currentIndex < 0)
                        currentIndex = 0;

                    int selectedIndex = currentIndex;
                    using (new ImGuiDisabledScope(!canWrite || enumNames.Length == 0))
                    {
                        string preview = hasMixedValues ? "<multiple>" : (enumNames.Length > 0 ? enumNames[Math.Clamp(selectedIndex, 0, enumNames.Length - 1)] : string.Empty);
                        if (enumNames.Length > 0 && ImGui.BeginCombo("##Value", preview))
                        {
                            for (int i = 0; i < enumNames.Length; i++)
                            {
                                bool selected = i == selectedIndex && !hasMixedValues;
                                if (ImGui.Selectable(enumNames[i], selected))
                                {
                                    object newValue = Enum.Parse(effectiveType, enumNames[i]);
                                    if (TryApplyInspectorValue(targets, property, values, newValue))
                                    {
                                        currentValue = newValue;
                                        isCurrentlyNull = false;
                                        hasMixedValues = false;
                                        selectedIndex = i;
                                    }
                                }
                            }
                            ImGui.EndCombo();
                        }
                    }
                    handled = true;
                }
            }
            else if (effectiveType == typeof(string))
            {
                string textValue = currentValue as string ?? string.Empty;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputTextWithHint("##Value", hasMixedValues ? "<multiple values>" : string.Empty, ref textValue, 512u, ImGuiInputTextFlags.None) && canWrite)
                    {
                        if (TryApplyInspectorValue(targets, property, values, textValue))
                        {
                            currentValue = textValue;
                            isCurrentlyNull = false;
                            hasMixedValues = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(Vector2))
            {
                Vector2 vector = currentValue is Vector2 v ? v : Vector2.Zero;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat2("##Value", ref vector, 0.05f) && canWrite)
                    {
                        if (TryApplyInspectorValue(targets, property, values, vector))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                            hasMixedValues = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(Vector3))
            {
                Vector3 vector = currentValue is Vector3 v ? v : Vector3.Zero;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat3("##Value", ref vector, 0.05f) && canWrite)
                    {
                        if (TryApplyInspectorValue(targets, property, values, vector))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                            hasMixedValues = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(Vector4))
            {
                Vector4 vector = currentValue is Vector4 v ? v : Vector4.Zero;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat4("##Value", ref vector, 0.05f) && canWrite)
                    {
                        if (TryApplyInspectorValue(targets, property, values, vector))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                            hasMixedValues = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(LayerMask))
            {
                int maskValue = currentValue is LayerMask mask ? mask.Value : 0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputInt("##Value", ref maskValue) && canWrite)
                    {
                        var newMask = new LayerMask(maskValue);
                        if (TryApplyInspectorValue(targets, property, values, newMask))
                        {
                            currentValue = newMask;
                            isCurrentlyNull = false;
                            hasMixedValues = false;
                        }
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Layer bitmask. -1 = all layers.");
                }
                handled = true;
            }
            else if (TryDrawColorProperty(targets, property, effectiveType, canWrite, values, ref currentValue, ref isCurrentlyNull))
            {
                handled = true;
            }
            else if (TryDrawNumericProperty(targets, property, effectiveType, canWrite, values, ref currentValue, ref isCurrentlyNull))
            {
                handled = true;
            }

            if (!handled)
            {
                if (currentValue is null)
                    ImGui.TextDisabled("<null>");
                else
                    ImGui.TextUnformatted(FormatSettingValue(currentValue));
            }

            if (isNullable && canWrite)
            {
                if (isCurrentlyNull)
                {
                    if (TryGetDefaultValue(effectiveType, out var defaultValue))
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Set"))
                        {
                            if (TryApplyInspectorValue(targets, property, values, defaultValue))
                            {
                                currentValue = defaultValue;
                                isCurrentlyNull = false;
                                hasMixedValues = false;
                            }
                        }
                    }
                }
                else
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Clear"))
                    {
                        if (TryApplyInspectorValue(targets, property, values, null))
                        {
                            currentValue = null;
                            isCurrentlyNull = true;
                            hasMixedValues = false;
                        }
                    }
                }
            }

            ImGui.PopID();
        }

        private static void DrawSimpleFieldRow(InspectorTargetSet targets, FieldInfo field, IReadOnlyList<object?> values, string displayName, string? description, bool valueRetrievalFailed, bool canWrite)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSimpleFieldRow");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(displayName);
            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);
            ImGui.TableSetColumnIndex(1);

            ImGui.PushID(field.Name);

            if (valueRetrievalFailed)
            {
                ImGui.TextDisabled("<error>");
                ImGui.PopID();
                return;
            }

            Type fieldType = field.FieldType;
            Type? underlyingType = Nullable.GetUnderlyingType(fieldType);
            bool isNullable = underlyingType is not null || !fieldType.IsValueType;
            Type effectiveType = underlyingType ?? fieldType;

            object? firstValue = values.Count > 0 ? values[0] : null;
            bool hasMixedValues = values.Skip(1).Any(v => !Equals(v, firstValue));
            object? currentValue = firstValue;
            bool isCurrentlyNull = currentValue is null;

            // Asset reference fields
            if (typeof(XRAsset).IsAssignableFrom(effectiveType))
            {
                XRAsset? currentAsset = firstValue as XRAsset;
                Type declaredAssetType = effectiveType;

                bool allowClear = canWrite && isNullable;
                bool allowCreateReplace = canWrite;

                if (!DrawAssetFieldForCollection(field.Name, declaredAssetType, currentAsset, selected =>
                {
                    try
                    {
                        foreach (var target in targets.Targets)
                            field.SetValue(target, selected);
                        NotifyInspectorValueEdited(targets);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, $"Failed to set asset value for field '{field.Name}'.");
                    }
                }, allowClear: allowClear, allowCreateOrReplace: allowCreateReplace))
                    ImGui.TextDisabled("Asset editor unavailable for this type.");

                ImGui.PopID();
                return;
            }

            bool handled = false;
            bool Apply(object? newValue)
            {
                if (!TryApplyInspectorValue(targets, field, values, newValue))
                    return false;
                currentValue = newValue;
                isCurrentlyNull = newValue is null;
                hasMixedValues = false;
                return true;
            }

            using (new ImGuiDisabledScope(!canWrite))
            {
                if (hasMixedValues)
                    ImGui.SetNextItemWidth(-1f);

                handled = DrawInlineValueEditor(effectiveType, canWrite, ref currentValue, ref isCurrentlyNull, Apply, "##Value");

                if (!handled)
                {
                    if (currentValue is null)
                        ImGui.TextDisabled("<null>");
                    else
                        ImGui.TextUnformatted(FormatSettingValue(currentValue));
                }
            }

            if (isNullable && canWrite)
            {
                if (isCurrentlyNull)
                {
                    if (TryGetDefaultValue(effectiveType, out var defaultValue))
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Set"))
                            Apply(defaultValue);
                    }
                }
                else
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Clear"))
                        Apply(null);
                }
            }

            ImGui.PopID();
        }

        private static void DrawNullComplexField(object owner, FieldInfo field, string displayName, string? description, bool canWrite)
        {
            Type fieldType = field.FieldType;
            Type effectiveType = Nullable.GetUnderlyingType(fieldType) ?? fieldType;
            bool isNullable = Nullable.GetUnderlyingType(fieldType) is not null || !fieldType.IsValueType;

            ImGui.PushID(field.Name);

            ImGui.TextUnformatted($"{displayName}:");
            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);

            ImGui.SameLine();
            ImGui.TextDisabled("<null>");

            if (canWrite && isNullable)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Create"))
                {
                    try
                    {
                        object? instance = Activator.CreateInstance(effectiveType);
                        if (instance is not null)
                        {
                            field.SetValue(owner, instance);
                            NotifyInspectorValueEdited(owner);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, $"Failed to create and set instance of '{effectiveType.FullName}' for field '{field.Name}'.");
                    }
                }
            }

            ImGui.PopID();
        }

        private static void DrawComplexFieldObject(object owner, FieldInfo field, object value, string label, string? description, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawComplexFieldObject");
            if (!visited.Add(value))
            {
                ImGui.TextUnformatted($"{label}: <circular reference>");
                return;
            }

            ImGui.PushID(field.Name);
            string treeLabel = $"{label} ({value.GetType().Name})";
            bool open = ImGui.TreeNodeEx(treeLabel, ImGuiTreeNodeFlags.None);
            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);

            if (open)
            {
                DrawSettingsProperties(new InspectorTargetSet(new[] { value }, value.GetType()), visited);
                ImGui.TreePop();
            }

            ImGui.PopID();
            visited.Remove(value);
        }

        private static bool TryDrawXREventMember(object owner, InspectorMemberRow row, object? currentValue, HashSet<object> visited)
        {
            PropertyInfo? property = row.Property?.Property;
            FieldInfo? field = row.Field?.Field;

            Type declaredType = property?.PropertyType ?? field?.FieldType ?? typeof(object);
            if (!IsXREventType(declaredType))
                return false;

            bool canWrite = property is not null
                ? (property.CanWrite && property.SetMethod?.IsPublic == true)
                : (row.Field?.CanWrite ?? false);

            ImGui.PushID(row.MemberName);

            object? value = currentValue;
            if (value is null)
            {
                ImGui.TextUnformatted($"{row.DisplayName}:");
                if (!string.IsNullOrEmpty(row.Description) && ImGui.IsItemHovered())
                    ImGui.SetTooltip(row.Description);

                ImGui.SameLine();
                ImGui.TextDisabled("<null>");

                if (canWrite)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Create"))
                    {
                        try
                        {
                            object? instance = Activator.CreateInstance(declaredType);
                            if (instance is not null)
                            {
                                if (property is not null)
                                    property.SetValue(owner, instance);
                                else
                                    field!.SetValue(owner, instance);

                                NotifyInspectorValueEdited(owner);
                                value = instance;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex, $"Failed to create XREvent instance for '{row.MemberName}'.");
                        }
                    }
                }

                ImGui.PopID();
                return true;
            }

            string typeLabel = declaredType == typeof(XREvent)
                ? "XREvent"
                : $"XREvent<{declaredType.GetGenericArguments()[0].Name}>";

            string treeLabel = $"{row.DisplayName} ({typeLabel})";

            bool open;
            if (ImGui.BeginTable("XREventHeader", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
            {
                ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 0f);
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                open = ImGui.TreeNodeEx(treeLabel, ImGuiTreeNodeFlags.SpanAvailWidth);
                if (!string.IsNullOrEmpty(row.Description) && ImGui.IsItemHovered())
                    ImGui.SetTooltip(row.Description);

                ImGui.TableSetColumnIndex(1);
                bool isEmpty = IsXREventEmpty(value);
                if (canWrite)
                {
                    using (new ImGuiDisabledScope(!isEmpty))
                    {
                        if (ImGui.SmallButton("Set Null"))
                        {
                            if (property is not null)
                                property.SetValue(owner, null);
                            else
                                field!.SetValue(owner, null);

                            NotifyInspectorValueEdited(owner);

                            if (open)
                                ImGui.TreePop();

                            ImGui.EndTable();
                            ImGui.PopID();
                            return true;
                        }
                    }
                }

                ImGui.EndTable();
            }
            else
            {
                open = ImGui.TreeNodeEx(treeLabel, ImGuiTreeNodeFlags.SpanAvailWidth);
                if (!string.IsNullOrEmpty(row.Description) && ImGui.IsItemHovered())
                    ImGui.SetTooltip(row.Description);
            }

            if (open)
            {
                DrawXREventInspector(owner, value, declaredType);
                ImGui.TreePop();
            }

            ImGui.PopID();
            return true;
        }

        private static bool IsXREventType(Type t)
            => t == typeof(XREvent)
            || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(XREvent<>));

        private static bool IsXREventEmpty(object eventInstance)
        {
            try
            {
                Type t = eventInstance.GetType();

                int count = 0;
                var countProp = t.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                if (countProp?.PropertyType == typeof(int))
                    count = (int)(countProp.GetValue(eventInstance) ?? 0);
                if (count != 0)
                    return false;

                bool hasPendingAdds = false;
                var pendingAddsProp = t.GetProperty("HasPendingAdds", BindingFlags.Public | BindingFlags.Instance);
                if (pendingAddsProp?.PropertyType == typeof(bool))
                    hasPendingAdds = (bool)(pendingAddsProp.GetValue(eventInstance) ?? false);
                if (hasPendingAdds)
                    return false;

                bool hasPendingRemoves = false;
                var pendingRemovesProp = t.GetProperty("HasPendingRemoves", BindingFlags.Public | BindingFlags.Instance);
                if (pendingRemovesProp?.PropertyType == typeof(bool))
                    hasPendingRemoves = (bool)(pendingRemovesProp.GetValue(eventInstance) ?? false);
                if (hasPendingRemoves)
                    return false;

                var calls = GetPersistentCallsOrNull(eventInstance);
                return calls is null || calls.Count == 0;
            }
            catch
            {
                return false;
            }
        }

        private readonly struct EventSignatureOption
        {
            public EventSignatureOption(Type[] paramTypes, bool tupleExpanded)
            {
                ParamTypes = paramTypes;
                TupleExpanded = tupleExpanded;
            }

            public Type[] ParamTypes { get; }
            public bool TupleExpanded { get; }
        }

        private sealed class EventMethodOption
        {
            public required XRObjectBase TargetObject { get; init; }
            public required string GroupLabel { get; init; }
            public required MethodInfo Method { get; init; }
            public required EventSignatureOption Signature { get; init; }

            public string DisplayLabel
                => FormatMethodLabel(Method);
        }

        private static void DrawXREventInspector(object owner, object eventInstance, Type declaredEventType)
        {
            ImGui.PushID("XREventPersistentCalls");

            var calls = GetPersistentCallsOrNull(eventInstance);
            int callCount = calls?.Count ?? 0;

            if (ImGui.SmallButton("Add Callback"))
            {
                calls = GetOrCreatePersistentCalls(eventInstance, owner);
                calls.Add(new XRPersistentCall());
                NotifyInspectorValueEdited(owner);
                callCount = calls.Count;
            }

            ImGui.SameLine();
            ImGui.TextDisabled(callCount == 1 ? "1 callback" : $"{callCount} callbacks");

            if (calls is null || calls.Count == 0)
            {
                ImGui.PopID();
                return;
            }

            var signatureOptions = GetEventSignatureOptions(declaredEventType);

            const ImGuiTableFlags tableFlags = ImGuiTableFlags.RowBg
                | ImGuiTableFlags.BordersInnerV
                | ImGuiTableFlags.SizingStretchProp;

            if (ImGui.BeginTable("CallbacksTable", 3, tableFlags))
            {
                ImGui.TableSetupColumn("Node", ImGuiTableColumnFlags.WidthStretch, 0.45f);
                ImGui.TableSetupColumn("Method", ImGuiTableColumnFlags.WidthStretch, 0.50f);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 32f);
                ImGui.TableHeadersRow();

                XRWorldInstance? world = (owner as XRComponent)?.SceneNode?.World
                    ?? (owner as SceneNode)?.World
                    ?? Selection.LastSceneNode?.World;

                for (int i = 0; i < calls.Count; i++)
                {
                    var call = calls[i];
                    ImGui.PushID(i);

                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    DrawPersistentCallNodePickerButton(owner, call, world);

                    ImGui.TableSetColumnIndex(1);
                    DrawPersistentCallMethodCombo(owner, call, signatureOptions);

                    ImGui.TableSetColumnIndex(2);
                    float removeButtonSize = ImGui.GetFrameHeight();
                    if (ImGui.Button("X", new Vector2(removeButtonSize, removeButtonSize)))
                    {
                        calls.RemoveAt(i);
                        NotifyInspectorValueEdited(owner);
                        ImGui.PopID();
                        i--;
                        continue;
                    }

                    ImGui.PopID();
                }

                ImGui.EndTable();
            }

            ImGui.PopID();
        }

        private static List<XRPersistentCall>? GetPersistentCallsOrNull(object eventInstance)
        {
            var prop = eventInstance.GetType().GetProperty("PersistentCalls", BindingFlags.Public | BindingFlags.Instance);
            return prop?.GetValue(eventInstance) as List<XRPersistentCall>;
        }

        private static List<XRPersistentCall> GetOrCreatePersistentCalls(object eventInstance, object owner)
        {
            var prop = eventInstance.GetType().GetProperty("PersistentCalls", BindingFlags.Public | BindingFlags.Instance);
            var calls = prop?.GetValue(eventInstance) as List<XRPersistentCall>;
            if (calls is not null)
                return calls;

            calls = new List<XRPersistentCall>();
            prop?.SetValue(eventInstance, calls);
            NotifyInspectorValueEdited(owner);
            return calls;
        }

        private static EventSignatureOption[] GetEventSignatureOptions(Type declaredEventType)
        {
            if (declaredEventType == typeof(XREvent))
                return [new EventSignatureOption(Array.Empty<Type>(), tupleExpanded: false)];

            Type payloadType = declaredEventType.GetGenericArguments()[0];
            var options = new List<EventSignatureOption>
            {
                new EventSignatureOption([payloadType], tupleExpanded: false)
            };

            if (TryGetValueTupleElementTypes(payloadType, out var tupleTypes) && tupleTypes.Length > 0)
                options.Add(new EventSignatureOption(tupleTypes, tupleExpanded: true));

            return options.ToArray();
        }

        private static void DrawPersistentCallNodePickerButton(object owner, XRPersistentCall call, XRWorldInstance? world)
        {
            SceneNode? selectedNode = ResolveSceneNode(call.NodeId);
            string nodeLabel = selectedNode?.Name ?? (call.NodeId == Guid.Empty ? "<Select Node>" : "<Missing Node>");

            string nodePopup = "SelectNodePopup";
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.Button(nodeLabel, new Vector2(-1f, 0f)))
                ImGui.OpenPopup(nodePopup);

            if (ImGui.BeginPopup(nodePopup))
            {
                if (world is null)
                {
                    ImGui.TextDisabled("No active world available.");
                }
                else
                {
                    DrawSceneNodePicker(world, node =>
                    {
                        call.NodeId = node.ID;
                        call.TargetObjectId = Guid.Empty;
                        call.MethodName = null;
                        call.ParameterTypeNames = null;
                        call.UseTupleExpansion = false;
                        NotifyInspectorValueEdited(owner);
                    });
                }

                ImGui.EndPopup();
            }
        }

        private static void DrawPersistentCallMethodCombo(object owner, XRPersistentCall call, EventSignatureOption[] signatureOptions)
        {
            SceneNode? selectedNode = ResolveSceneNode(call.NodeId);

            var methodOptions = selectedNode is null
                ? new List<EventMethodOption>()
                : BuildEventMethodOptions(selectedNode, signatureOptions);

            string preview = GetPersistentCallPreview(call);
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("##Method", preview, ImGuiComboFlags.HeightLarge))
            {
                if (ImGui.SmallButton("Clear"))
                {
                    call.TargetObjectId = Guid.Empty;
                    call.MethodName = null;
                    call.ParameterTypeNames = null;
                    call.UseTupleExpansion = false;
                    NotifyInspectorValueEdited(owner);
                }

                ImGui.Separator();

                if (methodOptions.Count == 0)
                {
                    ImGui.TextDisabled(selectedNode is null ? "Select a node to choose methods." : "No compatible methods found.");
                }
                else
                {
                    foreach (var group in methodOptions.GroupBy(m => m.GroupLabel, StringComparer.Ordinal))
                    {
                        ImGui.Selectable(group.Key, false, ImGuiSelectableFlags.Disabled);
                        foreach (var opt in group)
                        {
                            bool selected = IsSamePersistentCall(call, opt);
                            string uniqueId = $"{opt.TargetObject.ID}:{opt.Method.MetadataToken}:{opt.Signature.TupleExpanded}";
                            if (ImGui.Selectable($"    {opt.DisplayLabel}##{uniqueId}", selected))
                            {
                                call.TargetObjectId = opt.TargetObject.ID;
                                call.MethodName = opt.Method.Name;
                                call.ParameterTypeNames = opt.Method.GetParameters().Select(p => p.ParameterType.AssemblyQualifiedName ?? p.ParameterType.FullName ?? p.ParameterType.Name).ToArray();
                                call.UseTupleExpansion = opt.Signature.TupleExpanded;
                                NotifyInspectorValueEdited(owner);
                            }
                        }

                        ImGui.Separator();
                    }
                }

                ImGui.EndCombo();
            }
        }

        private static bool IsSamePersistentCall(XRPersistentCall call, EventMethodOption opt)
        {
            if (call.TargetObjectId != opt.TargetObject.ID)
                return false;
            if (!string.Equals(call.MethodName, opt.Method.Name, StringComparison.Ordinal))
                return false;
            if (call.UseTupleExpansion != opt.Signature.TupleExpanded)
                return false;
            return true;
        }

        private static string GetPersistentCallPreview(XRPersistentCall call)
        {
            if (call.TargetObjectId == Guid.Empty || string.IsNullOrWhiteSpace(call.MethodName))
                return "<Not Set>";

            if (!XRObjectBase.ObjectsCache.TryGetValue(call.TargetObjectId, out var target))
                return $"<Missing Target>.{call.MethodName}";

            string typeName = target.GetType().Name;
            return $"{typeName}.{call.MethodName}";
        }

        private static SceneNode? ResolveSceneNode(Guid id)
        {
            if (id == Guid.Empty)
                return null;
            return XRObjectBase.ObjectsCache.TryGetValue(id, out var obj) ? obj as SceneNode : null;
        }

        private static void DrawSceneNodePicker(XRWorldInstance world, Action<SceneNode> onSelected)
        {
            ImGui.TextDisabled("Select a Scene Node:");
            ImGui.Separator();

            foreach (var root in world.RootNodes)
            {
                if (root is null)
                    continue;
                DrawSceneNodePickerTree(root, onSelected);
            }
        }

        private static void DrawSceneNodePickerTree(SceneNode node, Action<SceneNode> onSelected)
        {
            ImGui.PushID(node.ID.ToString());

            var children = node.Transform.Children;
            bool hasChildren = children is not null && children.Count > 0;
            ImGuiTreeNodeFlags flags = hasChildren ? ImGuiTreeNodeFlags.OpenOnArrow : (ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen);

            bool open = ImGui.TreeNodeEx("##Node", flags);
            ImGui.SameLine();
            if (ImGui.Selectable(node.Name ?? SceneNode.DefaultName))
            {
                onSelected(node);
                ImGui.CloseCurrentPopup();
                if (open && hasChildren)
                    ImGui.TreePop();
                ImGui.PopID();
                return;
            }

            if (hasChildren && open)
            {
                var snapshot = children.ToArray();
                foreach (var child in snapshot)
                {
                    if (child?.SceneNode is SceneNode childNode)
                        DrawSceneNodePickerTree(childNode, onSelected);
                }
                ImGui.TreePop();
            }

            ImGui.PopID();
        }

        private static List<EventMethodOption> BuildEventMethodOptions(SceneNode node, EventSignatureOption[] signatureOptions)
        {
            var results = new List<EventMethodOption>();

            // SceneNode methods
            results.AddRange(GetCompatibleMethods(node, "SceneNode", signatureOptions));

            // Component methods grouped by component
            foreach (var comp in node.Components)
            {
                if (comp is null)
                    continue;
                results.AddRange(GetCompatibleMethods(comp, comp.GetType().Name, signatureOptions));
            }

            // Stable ordering: group then method name
            return results
                .OrderBy(r => r.GroupLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Method.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<EventMethodOption> GetCompatibleMethods(XRObjectBase target, string groupLabel, EventSignatureOption[] signatureOptions)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var methods = target.GetType().GetMethods(flags)
                .Where(m => m.ReturnType == typeof(void))
                .Where(m => !m.IsSpecialName)
                .Where(m => !m.ContainsGenericParameters)
                .Where(m => m.GetParameters().All(p => !p.IsOut && !p.ParameterType.IsByRef))
                .ToArray();

            foreach (var method in methods)
            {
                var ps = method.GetParameters();
                foreach (var sig in signatureOptions)
                {
                    if (ps.Length != sig.ParamTypes.Length)
                        continue;

                    bool match = true;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (!ps[i].ParameterType.IsAssignableFrom(sig.ParamTypes[i]))
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        yield return new EventMethodOption
                        {
                            TargetObject = target,
                            GroupLabel = groupLabel,
                            Method = method,
                            Signature = sig
                        };
                    }
                }
            }
        }

        private static bool TryGetValueTupleElementTypes(Type t, out Type[] elementTypes)
        {
            elementTypes = Array.Empty<Type>();
            if (!IsValueTupleType(t))
                return false;

            var list = new List<Type>();
            FlattenValueTupleTypes(t, list);
            elementTypes = list.ToArray();
            return elementTypes.Length > 0;
        }

        private static bool IsValueTupleType(Type t)
            => t.IsValueType
            && t.IsGenericType
            && (t.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) ?? false);

        private static void FlattenValueTupleTypes(Type tupleType, List<Type> dest)
        {
            var args = tupleType.GetGenericArguments();
            if (args.Length == 0)
                return;

            // ValueTuple can nest in the 8th parameter (TRest)
            if (args.Length == 8 && IsValueTupleType(args[7]))
            {
                for (int i = 0; i < 7; i++)
                    dest.Add(args[i]);
                FlattenValueTupleTypes(args[7], dest);
                return;
            }

            dest.AddRange(args);
        }

        private static string FormatMethodLabel(MethodInfo method)
        {
            var ps = method.GetParameters();
            if (ps.Length == 0)
                return method.Name + "()";

            string paramText = string.Join(", ", ps.Select(p => p.ParameterType.Name));
            return $"{method.Name}({paramText})";
        }

        private static void DrawOverrideableSettingRow(object owner, SettingPropertyDescriptor descriptor, IOverrideableSetting setting)
        {
            Type overrideType = setting.ValueType;
            Type? overrideUnderlying = Nullable.GetUnderlyingType(overrideType);
            Type overrideEffectiveType = overrideUnderlying ?? overrideType;
            bool overrideCanWrite = descriptor.Property.CanWrite && descriptor.Property.SetMethod?.IsPublic == true;

            object? overrideValue = setting.BoxedValue;
            bool overrideIsNull = overrideValue is null;
            bool hasOverride = setting.HasOverride;

            PropertyInfo? baseProperty = descriptor.PairedBaseProperty;
            object? baseValue = null;
            bool baseValueRetrievalFailed = false;
            bool baseIsNull = false;
            bool canWriteBase = false;
            Type? baseEffectiveType = null;

            if (baseProperty is not null)
            {
                try
                {
                    baseValue = baseProperty.GetValue(owner);
                    baseIsNull = baseValue is null;
                }
                catch
                {
                    baseValueRetrievalFailed = true;
                }

                canWriteBase = baseProperty.CanWrite && baseProperty.SetMethod?.IsPublic == true;
                baseEffectiveType = Nullable.GetUnderlyingType(baseProperty.PropertyType) ?? baseProperty.PropertyType;
            }

            ImGui.BeginGroup();

            if (baseProperty is not null)
            {
                if (baseValueRetrievalFailed)
                {
                    ImGui.TextDisabled("<base value error>");
                }
                else
                {
                    ImGui.TextDisabled("Base");
                    ImGui.SameLine();

                    ImGui.PushID("BaseValue");
                    if (baseEffectiveType is not null)
                    {
                        bool handled = DrawInlineValueEditor(baseEffectiveType, canWriteBase, ref baseValue, ref baseIsNull, newValue =>
                        {
                            if (baseProperty is null)
                                return false;

                            return TryApplyInspectorValue(owner, baseProperty, baseValue, newValue);
                        }, "##BaseValue");

                        if (!handled)
                            ImGui.TextUnformatted(FormatSettingValue(baseValue));
                    }
                    ImGui.PopID();
                }
            }

            ImGui.TextDisabled("Override");
            ImGui.SameLine(0f, 6f);
            bool checkboxValue = hasOverride;
            if (ImGui.Checkbox("##HasOverride", ref checkboxValue) && overrideCanWrite)
            {
                setting.HasOverride = checkboxValue;
                hasOverride = checkboxValue;
                NotifyInspectorValueEdited(owner);
            }

            ImGui.SameLine(0f, 8f);

            using (new ImGuiDisabledScope(!overrideCanWrite || !hasOverride))
            {
                ImGui.PushID("OverrideValue");
                bool handled = DrawInlineValueEditor(overrideEffectiveType, overrideCanWrite && hasOverride, ref overrideValue, ref overrideIsNull, newValue =>
                {
                    if (!overrideCanWrite || !hasOverride)
                        return false;

                    setting.BoxedValue = newValue;
                    NotifyInspectorValueEdited(owner);
                    return true;
                }, "##OverrideValue");
                if (!handled)
                    ImGui.TextUnformatted(FormatSettingValue(overrideValue));
                ImGui.PopID();
            }

            ImGui.SameLine();
            object? effectiveValue = hasOverride ? overrideValue : baseValue;
            ImGui.TextDisabled($"Effective: {FormatSettingValue(effectiveValue)}");

            ImGui.EndGroup();
        }

        private static bool DrawInlineValueEditor(Type effectiveType, bool canWrite, ref object? currentValue, ref bool isCurrentlyNull, Func<object?, bool> applyValue, string label)
        {
            if (effectiveType == typeof(bool))
            {
                bool boolValue = currentValue is bool b && b;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    if (ImGui.Checkbox(label, ref boolValue) && canWrite)
                    {
                        if (applyValue(boolValue))
                        {
                            currentValue = boolValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType.IsEnum)
            {
                if (effectiveType.IsDefined(typeof(FlagsAttribute), inherit: false))
                {
                    ulong bits = 0;
                    try
                    {
                        if (currentValue is not null)
                            bits = Convert.ToUInt64(currentValue, CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        bits = 0;
                    }

                    Array values = Enum.GetValues(effectiveType);
                    string preview = FormatFlagsEnumPreview(effectiveType, values, bits);

                    using (new ImGuiDisabledScope(!canWrite))
                    {
                        ImGui.SetNextItemWidth(-1f);
                        ImGui.SetNextWindowSizeConstraints(new Vector2(420.0f, 0.0f), new Vector2(float.MaxValue, float.MaxValue));
                        if (ImGui.BeginCombo(label, preview, ImGuiComboFlags.HeightLarge))
                        {
                            if (ImGui.SmallButton("Clear"))
                            {
                                ulong newBits = 0;
                                object newValue = Enum.ToObject(effectiveType, newBits);
                                if (applyValue(newValue))
                                {
                                    currentValue = newValue;
                                    isCurrentlyNull = false;
                                    bits = newBits;
                                }
                            }

                            ImGui.Separator();

                            for (int i = 0; i < values.Length; i++)
                            {
                                object raw = values.GetValue(i)!;
                                ulong flag;
                                try
                                {
                                    flag = Convert.ToUInt64(raw, CultureInfo.InvariantCulture);
                                }
                                catch
                                {
                                    continue;
                                }

                                if (flag == 0)
                                    continue;

                                bool isSet = (bits & flag) == flag;
                                string name = Enum.GetName(effectiveType, raw) ?? raw.ToString() ?? $"0x{flag:X}";

                                bool checkedNow = isSet;
                                if (ImGui.Checkbox(name, ref checkedNow))
                                {
                                    ulong newBits = checkedNow ? (bits | flag) : (bits & ~flag);
                                    object newValue = Enum.ToObject(effectiveType, newBits);
                                    if (applyValue(newValue))
                                    {
                                        currentValue = newValue;
                                        isCurrentlyNull = false;
                                        bits = newBits;
                                    }
                                }
                            }

                            ImGui.EndCombo();
                        }
                    }

                    return true;
                }
                else
                {
                    string[] enumNames = Enum.GetNames(effectiveType);
                    int currentIndex = currentValue is null ? -1 : Array.IndexOf(enumNames, Enum.GetName(effectiveType, currentValue));
                    if (currentIndex < 0)
                        currentIndex = 0;

                    int selectedIndex = currentIndex;
                    using (new ImGuiDisabledScope(!canWrite || enumNames.Length == 0))
                    {
                        if (enumNames.Length > 0 && ImGui.Combo(label, ref selectedIndex, enumNames, enumNames.Length) && canWrite && selectedIndex >= 0 && selectedIndex < enumNames.Length)
                        {
                            object newValue = Enum.Parse(effectiveType, enumNames[selectedIndex]);
                            if (applyValue(newValue))
                            {
                                currentValue = newValue;
                                isCurrentlyNull = false;
                            }
                        }
                    }

                    return true;
                }
            }

            if (effectiveType == typeof(string))
            {
                string textValue = currentValue as string ?? string.Empty;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputText(label, ref textValue, 512u, ImGuiInputTextFlags.None) && canWrite)
                    {
                        if (applyValue(textValue))
                        {
                            currentValue = textValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(Vector2))
            {
                Vector2 vector = currentValue is Vector2 v ? v : Vector2.Zero;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat2(label, ref vector, 0.05f) && canWrite)
                    {
                        if (applyValue(vector))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(Vector3))
            {
                Vector3 vector = currentValue is Vector3 v ? v : Vector3.Zero;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat3(label, ref vector, 0.05f) && canWrite)
                    {
                        if (applyValue(vector))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(Vector4))
            {
                Vector4 vector = currentValue is Vector4 v ? v : Vector4.Zero;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat4(label, ref vector, 0.05f) && canWrite)
                    {
                        if (applyValue(vector))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(LayerMask))
            {
                int maskValue = currentValue is LayerMask mask ? mask.Value : 0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputInt(label, ref maskValue) && canWrite)
                    {
                        var newMask = new LayerMask(maskValue);
                        if (applyValue(newMask))
                        {
                            currentValue = newMask;
                            isCurrentlyNull = false;
                        }
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Layer bitmask. -1 = all layers.");
                }
                return true;
            }

            const ImGuiColorEditFlags ColorPickerFlags = ImGuiColorEditFlags.Float | ImGuiColorEditFlags.HDR | ImGuiColorEditFlags.NoOptions;

            if (effectiveType == typeof(ColorF3))
            {
                Vector3 colorVec = currentValue is ColorF3 color ? new(color.R, color.G, color.B) : Vector3.Zero;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.ColorEdit3(label, ref colorVec, ColorPickerFlags) && canWrite)
                    {
                        var newColor = new ColorF3(colorVec.X, colorVec.Y, colorVec.Z);
                        if (applyValue(newColor))
                        {
                            currentValue = newColor;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(ColorF4))
            {
                Vector4 colorVec = currentValue is ColorF4 color ? new(color.R, color.G, color.B, color.A) : new Vector4(0f, 0f, 0f, 1f);
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.ColorEdit4(label, ref colorVec, ColorPickerFlags) && canWrite)
                    {
                        var newColor = new ColorF4(colorVec.X, colorVec.Y, colorVec.Z, colorVec.W);
                        if (applyValue(newColor))
                        {
                            currentValue = newColor;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (TryDrawNumericEditor(effectiveType, canWrite, ref currentValue, ref isCurrentlyNull, applyValue, label))
                return true;

            return false;
        }

        private static unsafe bool TryDrawNumericEditor(Type effectiveType, bool canWrite, ref object? currentValue, ref bool isCurrentlyNull, Func<object?, bool> applyValue, string label)
        {
            if (effectiveType == typeof(float))
            {
                float floatValue = currentValue is float f ? f : 0f;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputFloat(label, ref floatValue) && canWrite && float.IsFinite(floatValue))
                    {
                        if (applyValue(floatValue))
                        {
                            currentValue = floatValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(double))
            {
                double doubleValue = currentValue is double d ? d : 0.0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputDouble(label, ref doubleValue) && canWrite && double.IsFinite(doubleValue))
                    {
                        if (applyValue(doubleValue))
                        {
                            currentValue = doubleValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(decimal))
            {
                double doubleValue = currentValue is decimal dec ? (double)dec : 0.0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputDouble(label, ref doubleValue) && canWrite && double.IsFinite(doubleValue))
                    {
                        try
                        {
                            decimal newValue = Convert.ToDecimal(doubleValue);
                            if (applyValue(newValue))
                            {
                                currentValue = newValue;
                                isCurrentlyNull = false;
                            }
                        }
                        catch (OverflowException)
                        {
                            decimal clamped = doubleValue > 0 ? decimal.MaxValue : decimal.MinValue;
                            if (applyValue(clamped))
                            {
                                currentValue = clamped;
                                isCurrentlyNull = false;
                            }
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(int))
            {
                int intValue = currentValue is int i ? i : 0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.S32, ref intValue) && canWrite)
                    {
                        if (applyValue(intValue))
                        {
                            currentValue = intValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(uint))
            {
                uint uintValue = currentValue is uint u ? u : 0u;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.U32, ref uintValue) && canWrite)
                    {
                        if (applyValue(uintValue))
                        {
                            currentValue = uintValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(long))
            {
                long longValue = currentValue is long l ? l : 0L;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.S64, ref longValue) && canWrite)
                    {
                        if (applyValue(longValue))
                        {
                            currentValue = longValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(ulong))
            {
                ulong ulongValue = currentValue is ulong ul ? ul : 0UL;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.U64, ref ulongValue) && canWrite)
                    {
                        if (applyValue(ulongValue))
                        {
                            currentValue = ulongValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(short))
            {
                short shortValue = currentValue is short s ? s : (short)0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.S16, ref shortValue) && canWrite)
                    {
                        if (applyValue(shortValue))
                        {
                            currentValue = shortValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(ushort))
            {
                ushort ushortValue = currentValue is ushort us ? us : (ushort)0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.U16, ref ushortValue) && canWrite)
                    {
                        if (applyValue(ushortValue))
                        {
                            currentValue = ushortValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(byte))
            {
                byte byteValue = currentValue is byte by ? by : (byte)0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.U8, ref byteValue) && canWrite)
                    {
                        if (applyValue(byteValue))
                        {
                            currentValue = byteValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(sbyte))
            {
                sbyte sbyteValue = currentValue is sbyte sb ? sb : (sbyte)0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.S8, ref sbyteValue) && canWrite)
                    {
                        if (applyValue(sbyteValue))
                        {
                            currentValue = sbyteValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            return false;
        }

        private static unsafe bool TryDrawNumericProperty(InspectorTargetSet targets, PropertyInfo property, Type effectiveType, bool canWrite, IReadOnlyList<object?> previousValues, ref object? currentValue, ref bool isCurrentlyNull)
        {
            var previousValueList = new List<object?>(previousValues);

            bool Apply(object? newValue)
            {
                if (!TryApplyInspectorValue(targets, property, previousValueList, newValue))
                    return false;

                for (int i = 0; i < previousValueList.Count; i++)
                    previousValueList[i] = newValue;
                return true;
            }

            return TryDrawNumericEditor(effectiveType, canWrite, ref currentValue, ref isCurrentlyNull, Apply, "##Value");
        }

        private static bool TryDrawColorProperty(InspectorTargetSet targets, PropertyInfo property, Type effectiveType, bool canWrite, IReadOnlyList<object?> previousValues, ref object? currentValue, ref bool isCurrentlyNull)
        {
            const ImGuiColorEditFlags ColorPickerFlags = ImGuiColorEditFlags.Float | ImGuiColorEditFlags.HDR | ImGuiColorEditFlags.NoOptions;

            if (effectiveType == typeof(ColorF3))
            {
                Vector3 colorVec = currentValue is ColorF3 color ? new(color.R, color.G, color.B) : Vector3.Zero;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.ColorEdit3("##ColorValue", ref colorVec, ColorPickerFlags) && canWrite)
                    {
                        var newColor = new ColorF3(colorVec.X, colorVec.Y, colorVec.Z);
                        if (TryApplyInspectorValue(targets, property, previousValues, newColor))
                        {
                            currentValue = newColor;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(ColorF4))
            {
                Vector4 colorVec = currentValue is ColorF4 color ? new(color.R, color.G, color.B, color.A) : new Vector4(0f, 0f, 0f, 1f);
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.ColorEdit4("##ColorValue", ref colorVec, ColorPickerFlags) && canWrite)
                    {
                        var newColor = new ColorF4(colorVec.X, colorVec.Y, colorVec.Z, colorVec.W);
                        if (TryApplyInspectorValue(targets, property, previousValues, newColor))
                        {
                            currentValue = newColor;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            return false;
        }

        private static unsafe bool InputScalar<T>(string label, ImGuiDataType dataType, ref T value)
            where T : unmanaged
        {
            T localValue = value;
            void* ptr = Unsafe.AsPointer(ref localValue);
            bool changed = ImGui.InputScalar(label, dataType, new IntPtr(ptr));

            if (changed)
                value = localValue;

            return changed;
        }

        private static bool IsPathUnderDirectory(string candidatePath, string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootDirectory))
                return false;

            // Normalize to ensure consistent prefix checking.
            string root = Path.GetFullPath(rootDirectory);
            if (!root.EndsWith(Path.DirectorySeparatorChar))
                root += Path.DirectorySeparatorChar;

            string candidate = Path.GetFullPath(candidatePath);
            if (!candidate.EndsWith(Path.DirectorySeparatorChar))
                candidate += Path.DirectorySeparatorChar;

            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private readonly struct ImGuiDisabledScope : IDisposable
        {
            private readonly bool _disabled;

            public ImGuiDisabledScope(bool disabled)
            {
                _disabled = disabled;
                if (disabled)
                    ImGui.BeginDisabled();
            }

            public void Dispose()
            {
                if (_disabled)
                    ImGui.EndDisabled();
            }
        }

        internal static IDisposable PushInspectorAssetContext(XRAsset? asset)
            => new InspectorAssetContextScope(asset);

        private readonly struct InspectorAssetContextScope : IDisposable
        {
            private readonly XRAsset? _previous;

            public InspectorAssetContextScope(XRAsset? asset)
            {
                _previous = _inspectorAssetContext;
                _inspectorAssetContext = asset;
            }

            public void Dispose()
            {
                _inspectorAssetContext = _previous;
            }
        }

        private static bool TryGetDefaultValue(Type type, out object? value)
        {
            if (type == typeof(bool))
            {
                value = false;
                return true;
            }

            if (type == typeof(float))
            {
                value = 0f;
                return true;
            }

            if (type == typeof(double))
            {
                value = 0.0;
                return true;
            }

            if (type == typeof(decimal))
            {
                value = 0m;
                return true;
            }

            if (type == typeof(int))
            {
                value = 0;
                return true;
            }

            if (type == typeof(uint))
            {
                value = 0u;
                return true;
            }

            if (type == typeof(long))
            {
                value = 0L;
                return true;
            }

            if (type == typeof(ulong))
            {
                value = 0UL;
                return true;
            }

            if (type == typeof(short))
            {
                value = (short)0;
                return true;
            }

            if (type == typeof(ushort))
            {
                value = (ushort)0;
                return true;
            }

            if (type == typeof(byte))
            {
                value = (byte)0;
                return true;
            }

            if (type == typeof(sbyte))
            {
                value = (sbyte)0;
                return true;
            }

            if (type.IsEnum)
            {
                string[] names = Enum.GetNames(type);
                if (names.Length > 0)
                {
                    value = Enum.Parse(type, names[0]);
                    return true;
                }
            }

            value = null;
            return false;
        }

        private static bool IsSimpleSettingType(Type type)
        {
            Type effectiveType = Nullable.GetUnderlyingType(type) ?? type;
            if (typeof(XRAsset).IsAssignableFrom(effectiveType))
                return true;
            if (effectiveType == typeof(string))
                return true;
            if (effectiveType.IsPrimitive || effectiveType.IsEnum)
                return true;
            if (effectiveType == typeof(decimal))
                return true;
            if (effectiveType == typeof(Vector2) || effectiveType == typeof(Vector3) || effectiveType == typeof(Vector4))
                return true;
            if (effectiveType.IsValueType)
                return true;
            return false;
        }

        private static string FormatSettingValue(object? value)
        {
            if (value is null)
                return "<null>";

            return value switch
            {
                bool b => b ? "True" : "False",
                float f => f.ToString("0.###", CultureInfo.InvariantCulture),
                double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                decimal m => m.ToString("0.###", CultureInfo.InvariantCulture),
                int i => i.ToString(CultureInfo.InvariantCulture),
                uint ui => ui.ToString(CultureInfo.InvariantCulture),
                long l => l.ToString(CultureInfo.InvariantCulture),
                ulong ul => ul.ToString(CultureInfo.InvariantCulture),
                short s => s.ToString(CultureInfo.InvariantCulture),
                ushort us => us.ToString(CultureInfo.InvariantCulture),
                byte by => by.ToString(CultureInfo.InvariantCulture),
                sbyte sb => sb.ToString(CultureInfo.InvariantCulture),
                Vector2 v2 => $"({v2.X:0.###}, {v2.Y:0.###})",
                Vector3 v3 => $"({v3.X:0.###}, {v3.Y:0.###}, {v3.Z:0.###})",
                Vector4 v4 => $"({v4.X:0.###}, {v4.Y:0.###}, {v4.Z:0.###}, {v4.W:0.###})",
                _ => value.ToString() ?? string.Empty
            };
        }

        private sealed record CollectionTypeDescriptor(Type Type, string DisplayName, string Namespace, string AssemblyName)
        {
            public string FullName => Type.FullName ?? Type.Name;
        }

        private sealed class SettingPropertyDescriptor
        {
            public required PropertyInfo Property { get; init; }
            public required IReadOnlyList<object?> Values { get; init; }
            public bool ValueRetrievalFailed { get; init; }
            public bool IsSimple { get; init; }
            public string? Category { get; init; }
            public string DisplayName { get; set; } = string.Empty;
            public string? Description { get; set; }
            public bool IsOverrideable { get; init; }
            public PropertyInfo? PairedBaseProperty { get; set; }
            public bool Hidden { get; set; }

            public object? Value => Values.Count > 0 ? Values[0] : null;
        }

        private sealed class SettingFieldDescriptor
        {
            public required FieldInfo Field { get; init; }
            public required List<object?> Values { get; init; }
            public bool ValueRetrievalFailed { get; init; }
            public bool IsSimple { get; init; }
            public string? Category { get; init; }
            public string DisplayName { get; init; } = string.Empty;
            public string? Description { get; init; }
            public bool CanWrite { get; init; }
            public bool Hidden { get; set; }

            public object? Value => Values.Count > 0 ? Values[0] : null;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();

        bool IEqualityComparer<object>.Equals(object? x, object? y)
            => ReferenceEquals(x, y);

        int IEqualityComparer<object>.GetHashCode(object obj)
            => RuntimeHelpers.GetHashCode(obj);
    }
}
