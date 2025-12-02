using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    private static void CreatePostProcessingSettingsEditor(SceneNode node, PropertyInfo prop, object?[]? objects)
    {
        EnsureVerticalLayout(node);

        var settings = ResolvePropertyValues<PostProcessingSettings>(prop, objects);
        if (settings.Count == 0)
        {
            AddInfoLabel(CreateInspectorCard(node), "Post processing settings unavailable.");
            return;
        }

        var tonemapCard = CreateInspectorCard(node);
        AddSectionHeader(tonemapCard, "General");
        var tonemapProp = typeof(PostProcessingSettings).GetProperty(nameof(PostProcessingSettings.Tonemapping));
        if (tonemapProp is not null)
            AddPropertyRow(tonemapCard, tonemapProp, settings.Cast<object?>().ToArray());

        BuildAmbientOcclusionEditor(node, settings);
    }

    private static void BuildAmbientOcclusionEditor(SceneNode parent, List<PostProcessingSettings> settings)
    {
        var aoCard = CreateInspectorCard(parent);
        AddSectionHeader(aoCard, "Ambient Occlusion");

        var aoInstances = new List<AmbientOcclusionSettings>();
        foreach (var entry in settings)
        {
            if (entry is null)
                continue;

            entry.AmbientOcclusion ??= new AmbientOcclusionSettings();
            aoInstances.Add(entry.AmbientOcclusion);
        }

        if (aoInstances.Count == 0)
        {
            AddInfoLabel(aoCard, "Ambient occlusion settings unavailable.");
            return;
        }

        var aoTargets = aoInstances.Cast<object?>().ToArray();
        var aoType = typeof(AmbientOcclusionSettings);

        AddPropertyRow(aoCard, aoType.GetProperty(nameof(AmbientOcclusionSettings.Enabled))!, aoTargets);
        AddPropertyRow(aoCard, aoType.GetProperty(nameof(AmbientOcclusionSettings.Type))!, aoTargets);

        var screenGroup = CreateAoTypeGroup(
            aoCard,
            "Screen Space",
            aoTargets,
            nameof(AmbientOcclusionSettings.Radius),
            nameof(AmbientOcclusionSettings.Power));

        var multiViewGroup = CreateAoTypeGroup(
            aoCard,
            "Multi-View",
            aoTargets,
            nameof(AmbientOcclusionSettings.Radius),
            nameof(AmbientOcclusionSettings.SecondaryRadius),
            nameof(AmbientOcclusionSettings.Bias),
            nameof(AmbientOcclusionSettings.Power),
            nameof(AmbientOcclusionSettings.MultiViewBlend),
            nameof(AmbientOcclusionSettings.MultiViewSpread),
            nameof(AmbientOcclusionSettings.DepthPhi),
            nameof(AmbientOcclusionSettings.NormalPhi));

        var spatialHashGroup = CreateAoTypeGroup(
            aoCard,
            "Spatial Hash",
            aoTargets,
            nameof(AmbientOcclusionSettings.Radius),
            nameof(AmbientOcclusionSettings.Power),
            nameof(AmbientOcclusionSettings.Samples),
            nameof(AmbientOcclusionSettings.SpatialHashSteps),
            nameof(AmbientOcclusionSettings.Bias),
            nameof(AmbientOcclusionSettings.SpatialHashCellSize),
            nameof(AmbientOcclusionSettings.SpatialHashMaxDistance),
            nameof(AmbientOcclusionSettings.Thickness),
            nameof(AmbientOcclusionSettings.DistanceIntensity),
            nameof(AmbientOcclusionSettings.SamplesPerPixel));

        var mismatchNode = aoCard.NewChild();
        mismatchNode.IsActiveSelf = false;
        AddInfoLabel(mismatchNode, "Select a supported AO type to edit its parameters.");

        var controller = aoCard.AddComponent<AmbientOcclusionInspectorController>()!;
        controller.ScreenSpaceGroup = screenGroup;
        controller.MultiViewGroup = multiViewGroup;
        controller.SpatialHashGroup = spatialHashGroup;
        controller.MultipleTypeMessage = mismatchNode;
        controller.Targets = aoInstances.ToArray();
    }

    private static SceneNode CreateAoTypeGroup(SceneNode parent, string title, object?[]? targets, params string[] propertyNames)
    {
        var groupNode = parent.NewChild();
        EnsureVerticalLayout(groupNode);
        AddSectionHeader(groupNode, title);
        groupNode.IsActiveSelf = false;

        var aoType = typeof(AmbientOcclusionSettings);
        foreach (var propName in propertyNames)
        {
            var property = aoType.GetProperty(propName);
            if (property is null)
                continue;

            AddPropertyRow(groupNode, property, targets);
        }

        return groupNode;
    }

    private static void AddPropertyRow(SceneNode parent, PropertyInfo property, object?[]? targets, string? labelOverride = null)
    {
        if (property is null)
            return;

        var row = parent.NewChild();
        var splitter = row.SetTransform<UIDualSplitTransform>();
        splitter.VerticalSplit = false;
        splitter.FirstFixedSize = true;
        splitter.FixedSize = 170.0f;

        var labelNode = row.NewChild<UITextComponent>(out var label);
        label.Text = labelOverride ?? GetFriendlyPropertyName(property);
        label.FontSize = EditorUI.Styles.PropertyNameFontSize;
        label.HorizontalAlignment = EHorizontalAlignment.Right;
        label.VerticalAlignment = EVerticalAlignment.Center;
        label.Color = EditorUI.Styles.PropertyNameTextColor;
        label.BoundableTransform.Margins = new Vector4(4.0f, 2.0f, 8.0f, 2.0f);

        var valueNode = row.NewChild();
        var editor = CreateNew(property.PropertyType);
        if (editor is null)
        {
            AddInfoLabel(valueNode, $"No editor for {property.PropertyType.Name}.");
            return;
        }

        editor(valueNode, property, targets ?? Array.Empty<object?>());
    }

    private static string GetFriendlyPropertyName(PropertyInfo property)
    {
        var attr = property.GetCustomAttribute<DisplayNameAttribute>();
        if (!string.IsNullOrWhiteSpace(attr?.DisplayName))
            return attr.DisplayName;

        return InsertSpaces(property.Name);
    }

    private static string InsertSpaces(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        var builder = new StringBuilder(name.Length + 4);
        builder.Append(name[0]);
        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c) && !char.IsWhiteSpace(name[i - 1]))
                builder.Append(' ');
            builder.Append(c);
        }
        return builder.ToString();
    }
}
