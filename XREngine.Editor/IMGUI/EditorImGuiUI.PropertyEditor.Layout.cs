using ImGuiNET;
using System.Numerics;
using System.Reflection;
using XREngine.Core.Files;
using XREngine.Core.Reflection.Attributes;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    [ThreadStatic] private static List<InspectorMemberRow>[]? _inspectorRowsByDepth;
    [ThreadStatic] private static bool[][]? _inspectorVisibilityByDepth;
    private static readonly Dictionary<MemberInfo, string> _inspectorEditLabels = new();
    private static readonly Dictionary<MemberInfo, EnvironmentVariablePreferenceAttribute[]> _inspectorEnvironmentAttributes = new();

    /// <summary>Builds ordering and conditional override variants once per type.</summary>
    private static void InitializeInspectorLayout(CachedInspectorTypeLayout layout)
    {
        var rows = new List<InspectorMemberRow>(layout.Properties.Count + layout.Fields.Count);
        var propertiesByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var propertyRows = new InspectorMemberRow[layout.Properties.Count];
        for (int i = 0; i < layout.Properties.Count; i++)
        {
            CachedInspectorProperty cached = layout.Properties[i];
            propertyRows[i] = new InspectorMemberRow(CreateInspectorPropertyDescriptor(cached))
            {
                VisibilityIndex = i,
                CanClip = CanClipInspectorMember(cached.Property, cached.Property.PropertyType, cached.IsSimple, cached.IsOverrideable),
            };
            propertiesByName.TryAdd(cached.Property.Name, i);
        }

        for (int i = 0; i < propertyRows.Length; i++)
        {
            CachedInspectorProperty cached = layout.Properties[i];
            const string suffix = "Override";
            if (!cached.IsOverrideable || !cached.Property.Name.EndsWith(suffix, StringComparison.Ordinal)
                || !propertiesByName.TryGetValue(cached.Property.Name[..^suffix.Length], out int baseIndex))
                continue;

            // Pair only while BOTH conditional properties are visible. Keep an unpaired
            // variant when EditorBrowsableIf hides the base property.
            var descriptor = CreateInspectorPropertyDescriptor(cached);
            CachedInspectorProperty baseProperty = layout.Properties[baseIndex];
            descriptor.PairedBaseProperty = baseProperty.Property;
            descriptor.DisplayName = baseProperty.DisplayName;
            if (string.IsNullOrWhiteSpace(descriptor.Description))
                descriptor.Description = baseProperty.Description;
            rows.Add(new InspectorMemberRow(descriptor)
            {
                VisibilityIndex = i,
                PairedBaseIndex = baseIndex,
                IsPairedVariant = true,
            });
            propertyRows[i] = new InspectorMemberRow(propertyRows[i].Property!)
            {
                VisibilityIndex = i,
                PairedBaseIndex = baseIndex,
                HiddenByOverrideIndex = propertyRows[i].HiddenByOverrideIndex,
            };
            propertyRows[baseIndex].HiddenByOverrideIndex = i;
        }

        rows.AddRange(propertyRows);
        for (int i = 0; i < layout.Fields.Count; i++)
        {
            CachedInspectorField cached = layout.Fields[i];
            rows.Add(new InspectorMemberRow(new SettingFieldDescriptor
            {
                Field = cached.Field,
                IsSimple = cached.IsSimple,
                Category = cached.Category,
                DisplayName = cached.DisplayName,
                Description = cached.Description,
                CanWrite = cached.CanWrite,
            })
            {
                VisibilityIndex = layout.Properties.Count + i,
                CanClip = CanClipInspectorMember(cached.Field, cached.Field.FieldType, cached.IsSimple, false),
            });
        }
        rows.Sort(CompareInspectorRows);
        layout.Rows = rows.ToArray();
        layout.SearchRows = layout.Rows;
    }

    private static SettingPropertyDescriptor CreateInspectorPropertyDescriptor(CachedInspectorProperty cached)
        => new()
        {
            Property = cached.Property,
            IsSimple = cached.IsSimple,
            Category = cached.Category,
            DisplayName = cached.DisplayName,
            Description = cached.Description,
            IsOverrideable = cached.IsOverrideable,
        };

    /// <summary>Filters cached ordering into recursion-local storage; conditions and values remain live.</summary>
    private static List<InspectorMemberRow> GetVisibleInspectorRows(CachedInspectorTypeLayout layout, InspectorTargetSet targets, string search)
    {
        _inspectorRowsByDepth ??= new List<InspectorMemberRow>[MaxInspectorDepth];
        _inspectorVisibilityByDepth ??= new bool[MaxInspectorDepth][];
        int depth = _inspectorDepth - 1;
        List<InspectorMemberRow> rows = _inspectorRowsByDepth[depth] ??= new List<InspectorMemberRow>(layout.Rows.Length);
        rows.Clear();
        int memberCount = layout.Properties.Count + layout.Fields.Count;
        bool[] visible = _inspectorVisibilityByDepth[depth] ?? [];
        if (visible.Length < memberCount)
            _inspectorVisibilityByDepth[depth] = visible = new bool[memberCount];

        for (int i = 0; i < layout.Properties.Count; i++)
            visible[i] = IsEditorBrowsable(layout.Properties[i].EditorBrowsableCondition, targets.Targets);
        for (int i = 0; i < layout.Fields.Count; i++)
            visible[layout.Properties.Count + i] = IsEditorBrowsable(layout.Fields[i].EditorBrowsableCondition, targets.Targets);

        if (!StringComparer.Ordinal.Equals(layout.Search, search))
        {
            layout.Search = search;
            if (string.IsNullOrWhiteSpace(search))
                layout.SearchRows = layout.Rows;
            else
            {
                var matches = new List<InspectorMemberRow>();
                for (int i = 0; i < layout.Rows.Length; i++)
                    if (MatchesInspectorSearch(layout.Rows[i], search))
                        matches.Add(layout.Rows[i]);
                layout.SearchRows = matches.ToArray();
            }
        }

        IReadOnlyList<InspectorMemberRow> candidates = layout.SearchRows;
        for (int i = 0; i < candidates.Count; i++)
        {
            InspectorMemberRow row = candidates[i];
            if (!visible[row.VisibilityIndex]
                || (row.HiddenByOverrideIndex >= 0 && visible[row.HiddenByOverrideIndex])
                || (row.PairedBaseIndex >= 0 && visible[row.PairedBaseIndex] != row.IsPairedVariant))
                continue;
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Only single-line controls enter uniform clipping; previews and diagnostics keep their natural height.</summary>
    private static bool CanClipInspectorMember(MemberInfo member, Type type, bool isSimple, bool isOverrideable)
    {
        if (!isSimple || isOverrideable || typeof(XRAsset).IsAssignableFrom(type))
            return false;
        if (member.IsDefined(typeof(InspectorPathAttribute), true)
            || GetInspectorEnvironmentAttributes(member).Length != 0)
            return false;
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
            || type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Quaternion);
    }

    private static EnvironmentVariablePreferenceAttribute[] GetInspectorEnvironmentAttributes(MemberInfo member)
    {
        if (!_inspectorEnvironmentAttributes.TryGetValue(member, out var attributes))
        {
            attributes = member.GetCustomAttributes<EnvironmentVariablePreferenceAttribute>(true).ToArray();
            _inspectorEnvironmentAttributes.Add(member, attributes);
        }
        return attributes;
    }

    private static string GetInspectorEditLabel(MemberInfo member)
    {
        if (!_inspectorEditLabels.TryGetValue(member, out string? label))
            _inspectorEditLabels.Add(member, label = "Edit " + member.Name);
        return label;
    }

    /// <summary>Shared resizable proportions leave room for values in narrow docks.</summary>
    private static void SetupInspectorPropertyColumns()
    {
        ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthStretch, 0.38f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.62f);
    }

    private static bool HasMixedInspectorValues(IReadOnlyList<object?> values)
    {
        for (int i = 1; i < values.Count; i++)
            if (!Equals(values[0], values[i]))
                return true;
        return false;
    }

    /// <summary>The clipper maintains scroll extent and navigation; active edits keep every row submitted.</summary>
    private static unsafe void DrawClippedInspectorRows(InspectorTargetSet targets, List<InspectorMemberRow> rows, int start, int end)
    {
        if (ImGui.IsAnyItemActive() || ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
        {
            for (int i = start; i < end; i++)
                DrawInspectorSimpleRow(targets, rows[i]);
            return;
        }

        var clipper = new ImGuiListClipper();
        ImGuiNative.ImGuiListClipper_Begin(&clipper, end - start, -1f);
        try
        {
            while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    DrawInspectorSimpleRow(targets, rows[start + i]);
        }
        finally
        {
            ImGuiNative.ImGuiListClipper_End(&clipper);
        }
    }

    private static void DrawInspectorSimpleRow(InspectorTargetSet targets, InspectorMemberRow row)
    {
        if (row.Property is not null)
        {
            bool failed = !TryGetPropertyValues(targets, row.Property.Property, out InspectorValueBuffer values);
            if (!TryDrawFastInspectorRow(targets, row, values, failed))
                DrawSimplePropertyRow(targets, row.Property, values, failed);
        }
        else if (row.Field is not null)
        {
            bool failed = !TryGetFieldValues(targets, row.Field.Field, out InspectorValueBuffer values);
            if (!TryDrawFastInspectorRow(targets, row, values, failed))
                DrawSimpleFieldRow(targets, row.Field.Field, values, row.DisplayName, row.Description, failed, row.Field.CanWrite);
        }
    }

    /// <summary>A delegate-free path for the scalar fields most frequently inspected.</summary>
    private static bool TryDrawFastInspectorRow(InspectorTargetSet targets, InspectorMemberRow row, InspectorValueBuffer values, bool failed)
    {
        MemberInfo member = (MemberInfo?)row.Property?.Property ?? row.Field!.Field;
        Type type = row.Property?.Property.PropertyType ?? row.Field!.Field.FieldType;
        if (!row.CanClip || !(type == typeof(bool) || type == typeof(float) || type == typeof(double) || type == typeof(int)))
            return false;

        bool canWrite = row.Property is not null
            ? row.Property.Property.CanWrite && row.Property.Property.SetMethod?.IsPublic == true
            : row.Field!.CanWrite;
        BeginInspectorPropertyRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ActiveEditorPreferenceOverride? activeOverride = null;
        if (row.Property is not null)
            TryGetActiveEditorPreferenceOverride(targets, row.Property.Property, out activeOverride);
        DrawInspectorMemberLabel(member, row.DisplayName, row.Description, activeOverride);
        ImGui.TableSetColumnIndex(1);
        ImGui.PushID(member.Name);
        try
        {
            if (failed)
            {
                ImGui.TextDisabled("<error>");
                return true;
            }
            bool mixed = HasMixedInspectorValues(values);
            if (mixed && type != typeof(bool))
                DrawInspectorMixedValueMarker();
            ImGui.SetNextItemWidth(-1f);
            using var disabled = new ImGuiDisabledScope(!canWrite);
            bool edited;
            object? newValue = null;
            if (type == typeof(bool))
            {
                bool value = values[0] is true;
                edited = DrawInspectorMixedCheckbox("##Value", ref value, mixed);
                if (edited)
                    newValue = value;
            }
            else if (type == typeof(float))
            {
                float value = values[0] is float number ? number : 0f;
                edited = ImGui.InputFloat("##Value", ref value) && float.IsFinite(value);
                if (edited)
                    newValue = value;
            }
            else if (type == typeof(double))
            {
                double value = values[0] is double number ? number : 0d;
                edited = ImGui.InputDouble("##Value", ref value) && double.IsFinite(value);
                if (edited)
                    newValue = value;
            }
            else
            {
                int value = values[0] is int number ? number : 0;
                edited = InputScalar("##Value", ImGuiDataType.S32, ref value);
                if (edited)
                    newValue = value;
            }

            if (edited && canWrite)
            {
                if (row.Property is not null)
                    TryApplyInspectorValue(targets, row.Property.Property, values, newValue);
                else
                    TryApplyInspectorValue(targets, row.Field!.Field, values, newValue);
            }
            UpdateInspectorUndoScope(GetInspectorEditLabel(member), targets);
            return true;
        }
        finally
        {
            ImGui.PopID();
        }
    }

    private static void DrawInspectorMixedValueMarker()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("\u2014");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Multiple values. Editing applies to all selected objects.");
        ImGui.SameLine();
    }

    private static readonly Dictionary<Type, (string[] Names, Array Values)> _inspectorEnumCache = new();
    private static readonly Dictionary<(Type Type, ulong Bits), string> _inspectorFlagPreviewCache = new();

    private static (string[] Names, Array Values) GetInspectorEnum(Type type)
    {
        if (!_inspectorEnumCache.TryGetValue(type, out var values))
            _inspectorEnumCache.Add(type, values = (Enum.GetNames(type), Enum.GetValues(type)));
        return values;
    }

    private static void BeginInspectorPropertyRow()
        => ImGui.TableNextRow(ImGuiTableRowFlags.None, ImGui.GetFrameHeight() + ImGui.GetStyle().CellPadding.Y * 2f);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, InspectorHeaderSearchState>> InspectorCategorySearchStates = new();

    private static InspectorHeaderSearchState PrepareInspectorCategorySearch(object target, string? category, bool searching)
    {
        var categories = InspectorCategorySearchStates.GetValue(target, static _ => new Dictionary<string, InspectorHeaderSearchState>(StringComparer.OrdinalIgnoreCase));
        string key = category ?? string.Empty;
        if (!categories.TryGetValue(key, out var state))
            categories.Add(key, state = new InspectorHeaderSearchState());
        if (searching)
        {
            if (!state.SearchWasActive)
            {
                state.RestoreOpen = state.LastOpen;
                state.SearchWasActive = true;
            }
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        }
        else if (state.SearchWasActive)
        {
            ImGui.SetNextItemOpen(state.RestoreOpen, ImGuiCond.Always);
            state.SearchWasActive = false;
        }
        else
            ImGui.SetNextItemOpen(false, ImGuiCond.Once);
        return state;
    }
}
