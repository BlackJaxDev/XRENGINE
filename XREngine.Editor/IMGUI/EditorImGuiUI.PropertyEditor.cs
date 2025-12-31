using ImGuiNET;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Editor.ComponentEditors;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
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
        private static void DrawSettingsObject(object obj, string label, string? description, HashSet<object> visited, bool defaultOpen, string? idOverride = null)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSettingsObject");
            if (!visited.Add(obj))
            {
                ImGui.TextUnformatted($"{label}: <circular reference>");
                return;
            }

            if (obj is XRRenderPipelineInstance instance && instance.Pipeline is not null)
            {
                if (ImGui.SmallButton($"Open Pipeline Graph##{label}"))
                    OpenRenderPipelineGraph(instance.Pipeline);
                ImGui.SameLine();
                ImGui.TextDisabled(instance.DebugDescriptor);
            }

            string id = idOverride ?? label;
            ImGui.PushID(id);
            string treeLabel = $"{label} ({obj.GetType().Name})";
            bool open = ImGui.TreeNodeEx(treeLabel, defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);
            if (open)
            {
                bool handledByAssetInspector = false;
                if (obj is XRAsset asset)
                {
                    using var assetScope = new InspectorAssetContextScope(asset.SourceAsset ?? asset);

                    // Allow custom inspectors to draw even if this asset is already tracked in the visited set.
                    visited.Remove(obj);
                    try
                    {
                        handledByAssetInspector = TryDrawAssetInspector(asset, visited);
                    }
                    finally
                    {
                        visited.Add(obj);
                    }
                }

                if (!handledByAssetInspector)
                    DrawSettingsProperties(obj, visited);

                ImGui.TreePop();
            }
            ImGui.PopID();
            visited.Remove(obj);
        }

        internal static void DrawRuntimeObjectInspector(string label, object? target, HashSet<object> visited, bool defaultOpen = true, string? description = null)
        {
            if (target is null)
            {
                ImGui.TextDisabled($"{label}: <null>");
                return;
            }

            DrawSettingsObject(target, label, description, visited, defaultOpen);
        }

        private static void DrawSettingsProperties(object obj, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSettingsProperties");
            Type type = obj.GetType();
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

                    object? value = null;
                    bool valueRetrievalFailed = false;
                    try
                    {
                        value = p.GetValue(obj);
                    }
                    catch
                    {
                        valueRetrievalFailed = true;
                    }

                    bool isSimple = IsSimpleSettingType(p.PropertyType);

                    return new
                    {
                        Property = p,
                        Value = value,
                        ValueRetrievalFailed = valueRetrievalFailed,
                        IsSimple = isSimple,
                        Category = category,
                        DisplayName = displayName,
                        Description = description
                    };
                })
                .Where(x => x != null)
                .Select(x => x!)
                .ToList();

            if (propertyInfos.Count == 0)
            {
                ImGui.TextDisabled("No properties found.");
                return;
            }

            var orderedPropertyInfos = propertyInfos
                .OrderBy(info => string.IsNullOrWhiteSpace(info.Category) ? 0 : 1)
                .ThenBy(info => info.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(info => info.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var grouped = orderedPropertyInfos
                .GroupBy(info => info.Category, StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool multipleCategories = grouped.Count > 1;
            bool renderedCategoryHeader = false;

            foreach (var group in grouped)
            {
                string categoryLabel = string.IsNullOrWhiteSpace(group.Key) ? "General" : group.Key;

                var simpleProps = group.Where(info => info.IsSimple).ToList();
                var complexProps = group.Where(info => !info.IsSimple).ToList();

                if (multipleCategories || !string.IsNullOrWhiteSpace(group.Key))
                {
                    if (renderedCategoryHeader)
                        ImGui.Separator();
                    ImGui.TextUnformatted(categoryLabel);
                    renderedCategoryHeader = true;
                }

                if (simpleProps.Count > 0)
                {
                    string tableId = $"Properties_{obj.GetHashCode():X8}_{group.Key?.GetHashCode() ?? 0:X8}";
                    if (ImGui.BeginTable(tableId, 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
                    {
                        // Prevent the name column from collapsing to a few pixels.
                        // Without this, long value widgets can steal all width and labels get clipped to 1-2 chars.
                        ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthFixed, 280.0f);
                        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
                        foreach (var info in simpleProps)
                            DrawSimplePropertyRow(obj, info.Property, info.Value, info.DisplayName, info.Description, info.ValueRetrievalFailed);
                        ImGui.EndTable();
                    }
                }

                foreach (var info in complexProps)
                {
                    if (info.ValueRetrievalFailed)
                    {
                        ImGui.TextUnformatted($"{info.DisplayName}: <error>");
                        if (!string.IsNullOrEmpty(info.Description) && ImGui.IsItemHovered())
                            ImGui.SetTooltip(info.Description);
                        continue;
                    }

                    if (info.Value is null)
                    {
                        DrawNullComplexProperty(obj, info.Property, info.DisplayName, info.Description);
                        continue;
                    }

                    if (TryDrawCollectionProperty(obj, info.Property, info.DisplayName, info.Description, info.Value, visited))
                        continue;

                    // Handle GL objects with their custom ImGui editors
                    if (TryDrawGLObjectProperty(info.Property, info.DisplayName, info.Description, info.Value))
                        continue;

                    DrawComplexPropertyObject(obj, info.Property, info.Value, info.DisplayName, info.Description, visited);
                }
            }
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
                        handledByAssetInspector = TryDrawAssetInspector(asset, visited);
                    }
                    finally
                    {
                        visited.Add(value);
                    }
                }

                if (!handledByAssetInspector)
                    DrawSettingsProperties(value, visited);

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
                else if (ImGui.BeginTable("CollectionItems", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
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
                            ImGui.TableNextRow();
                            ImGui.TableSetColumnIndex(0);
                            ImGui.TextUnformatted($"[{i}]");
                            ImGui.TableSetColumnIndex(1);
                            ImGui.TextUnformatted($"<error: {ex.Message}>");
                            continue;
                        }

                        Type? runtimeType = item?.GetType();
                        bool itemIsAsset = runtimeType is not null
                            ? typeof(XRAsset).IsAssignableFrom(runtimeType)
                            : elementIsAsset;
                        bool itemUsesTypeSelector = runtimeType is not null
                            ? ShouldUseCollectionTypeSelector(runtimeType)
                            : elementUsesTypeSelector;

                        ImGui.TableNextRow();
                        ImGui.PushID(i);

                        ImGui.TableSetColumnIndex(0);
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

                        ImGui.TableSetColumnIndex(1);

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
                            DrawSettingsObject(item, $"{label}[{i}]", description, visited, false, property.Name + i.ToString(CultureInfo.InvariantCulture));
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

                        ImGui.PopID();
                    }

                    ImGui.EndTable();
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
                        object? entryValue = DictionaryContainsKey(dictionary, key) ? dictionary[key] : null;
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
                            DrawSettingsObject(entryValue, childLabel, description, visited, false, property.Name + "_" + i.ToString(CultureInfo.InvariantCulture));
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
                if (value is null)
                    return _list.Contains(converted);
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

                var assetTypeDescriptors = GetPropertyTypeDescriptors(effectiveType);
                Type assetEditorType = assetTypeDescriptors.Count > 0 ? assetTypeDescriptors[0].Type : effectiveType;

                // Draw asset field for null asset
                if (!DrawAssetFieldForProperty(property.Name, assetEditorType, null, owner, property))
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
            }))
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

        private static bool DrawAssetFieldForCollection(string id, Type assetType, XRAsset? current, Action<XRAsset?> assign)
        {
            if (!typeof(XRAsset).IsAssignableFrom(assetType))
                return false;
            if (assetType.IsAbstract || assetType.ContainsGenericParameters)
                return false;
            if (assetType.GetConstructor(Type.EmptyTypes) is null)
                return false;

            _drawAssetCollectionElementMethod.MakeGenericMethod(assetType).Invoke(null, new object?[] { id, current, assign });
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

        private static void DrawAssetCollectionElementGeneric<TAsset>(string id, XRAsset? currentBase, Action<XRAsset?> assign)
            where TAsset : XRAsset, new()
        {
            TAsset? typedCurrent = currentBase as TAsset;
            ImGuiAssetUtilities.DrawAssetField<TAsset>(id, typedCurrent, asset => assign(asset));
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

        private static bool TryApplyInspectorValue(object owner, PropertyInfo property, object? previousValue, object? newValue)
        {
            if (Equals(previousValue, newValue))
                return false;

            property.SetValue(owner, newValue);
            NotifyInspectorValueEdited(owner);
            return true;
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

        private static void DrawSimplePropertyRow(object owner, PropertyInfo property, object? value, string displayName, string? description, bool valueRetrievalFailed)
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

            Type propertyType = property.PropertyType;
            Type? underlyingType = Nullable.GetUnderlyingType(propertyType);
            bool isNullable = underlyingType is not null;
            Type effectiveType = underlyingType ?? propertyType;
            bool canWrite = property.CanWrite && property.SetMethod?.IsPublic == true;

            object? currentValue = value;
            bool isCurrentlyNull = currentValue is null;
            bool handled = false;

            if (effectiveType == typeof(bool))
            {
                bool boolValue = currentValue is bool b && b;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    if (ImGui.Checkbox("##Value", ref boolValue) && canWrite)
                    {
                        if (TryApplyInspectorValue(owner, property, currentValue, boolValue))
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
                // Support [Flags] enums (e.g. Assimp.PostProcessSteps) as a multi-select.
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
                        // Combo popup inherits the combo width by default, which can be quite narrow in tables.
                        // Give it a more usable minimum width so flag names/checkboxes are readable.
                        ImGui.SetNextWindowSizeConstraints(new Vector2(420.0f, 0.0f), new Vector2(float.MaxValue, float.MaxValue));
                        if (ImGui.BeginCombo("##Value", preview, ImGuiComboFlags.HeightLarge))
                        {
                            if (ImGui.SmallButton("Clear"))
                            {
                                ulong newBits = 0;
                                object newValue = Enum.ToObject(effectiveType, newBits);
                                if (TryApplyInspectorValue(owner, property, currentValue, newValue))
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

                                // Skip 0-value in the list; use Clear instead.
                                if (flag == 0)
                                    continue;

                                bool isSet = (bits & flag) == flag;
                                string name = Enum.GetName(effectiveType, raw) ?? raw.ToString() ?? $"0x{flag:X}";

                                bool checkedNow = isSet;
                                if (ImGui.Checkbox(name, ref checkedNow))
                                {
                                    ulong newBits = checkedNow ? (bits | flag) : (bits & ~flag);
                                    object newValue = Enum.ToObject(effectiveType, newBits);
                                    if (TryApplyInspectorValue(owner, property, currentValue, newValue))
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
                        if (enumNames.Length > 0 && ImGui.Combo("##Value", ref selectedIndex, enumNames, enumNames.Length) && canWrite && selectedIndex >= 0 && selectedIndex < enumNames.Length)
                        {
                            object newValue = Enum.Parse(effectiveType, enumNames[selectedIndex]);
                            if (TryApplyInspectorValue(owner, property, currentValue, newValue))
                            {
                                currentValue = newValue;
                                isCurrentlyNull = false;
                            }
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
                    if (ImGui.InputText("##Value", ref textValue, 512u, ImGuiInputTextFlags.None) && canWrite)
                    {
                        if (TryApplyInspectorValue(owner, property, currentValue, textValue))
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
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat2("##Value", ref vector, 0.05f) && canWrite)
                    {
                        if (TryApplyInspectorValue(owner, property, currentValue, vector))
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
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat3("##Value", ref vector, 0.05f) && canWrite)
                    {
                        if (TryApplyInspectorValue(owner, property, currentValue, vector))
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
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat4("##Value", ref vector, 0.05f) && canWrite)
                    {
                        if (TryApplyInspectorValue(owner, property, currentValue, vector))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
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
                        if (TryApplyInspectorValue(owner, property, currentValue, newMask))
                        {
                            currentValue = newMask;
                            isCurrentlyNull = false;
                        }
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Layer bitmask. -1 = all layers.");
                }
                handled = true;
            }
            else if (TryDrawColorProperty(owner, property, effectiveType, canWrite, ref currentValue, ref isCurrentlyNull))
            {
                handled = true;
            }
            else if (TryDrawNumericProperty(owner, property, effectiveType, canWrite, ref currentValue, ref isCurrentlyNull))
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
                            if (TryApplyInspectorValue(owner, property, currentValue, defaultValue))
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
                        if (TryApplyInspectorValue(owner, property, currentValue, null))
                        {
                            currentValue = null;
                            isCurrentlyNull = true;
                        }
                    }
                }
            }

            ImGui.PopID();
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

        private static unsafe bool TryDrawNumericProperty(object owner, PropertyInfo property, Type effectiveType, bool canWrite, ref object? currentValue, ref bool isCurrentlyNull)
        {
            var previousValue = currentValue;

            bool Apply(object? newValue)
            {
                if (!TryApplyInspectorValue(owner, property, previousValue, newValue))
                    return false;

                previousValue = newValue;
                return true;
            }

            return TryDrawNumericEditor(effectiveType, canWrite, ref currentValue, ref isCurrentlyNull, Apply, "##Value");
        }

        private static bool TryDrawColorProperty(object owner, PropertyInfo property, Type effectiveType, bool canWrite, ref object? currentValue, ref bool isCurrentlyNull)
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
                        if (TryApplyInspectorValue(owner, property, currentValue, newColor))
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
                        if (TryApplyInspectorValue(owner, property, currentValue, newColor))
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

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();

        bool IEqualityComparer<object>.Equals(object? x, object? y)
            => ReferenceEquals(x, y);

        int IEqualityComparer<object>.GetHashCode(object obj)
            => RuntimeHelpers.GetHashCode(obj);
    }
}
