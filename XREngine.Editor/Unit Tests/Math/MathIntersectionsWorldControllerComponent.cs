using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public sealed class MathIntersectionsWorldControllerComponent : XRComponent, IRenderable
{
    private const float TitleFontSize = 22.0f;
    private const float DescriptionFontSize = 15.0f;
    private const float LabelScale = 0.001f;
    private const float OutlineScaleMultiplier = 1.18f;
    private const float LabelHeightOffset = 0.85f;
    private const float TitleLineYOffset = 22.0f;
    private const float DescriptionLineYOffset = -18.0f;
    private const float SubLabelFontSize = 13.0f;
    private const float SubLabelScale = 0.00065f;

    private readonly List<MathIntersectionsWorldTestEntry> _tests = [];
    private readonly Dictionary<MathIntersectionsWorldTestEntry, MathIntersectionsWorldLabelSet> _labels = [];
    private readonly List<SubLabelDefinition> _subLabelDefs = [];
    private readonly Dictionary<SubLabelDefinition, SubLabelRenderable> _subLabels = [];
    private readonly RenderInfo3D _renderInfo;
    private CustomUIComponent? _customUi;
    private FontGlyphSet? _labelFont;

    public RenderInfo RenderInfo => _renderInfo;
    public RenderInfo[] RenderedObjects { get; }

    public MathIntersectionsWorldControllerComponent()
    {
        RenderedObjects = [_renderInfo = RenderInfo3D.New(this, EDefaultRenderPass.OnTopForward, RenderLabels)];
        _renderInfo.Layer = XREngine.Components.Scene.Transforms.DefaultLayers.GizmosIndex;
    }

    public void RegisterTest(SceneNode rootNode, string displayName, string description, AABB bounds)
    {
        var entry = new MathIntersectionsWorldTestEntry(rootNode, displayName, description, bounds, rootNode.GetTransformAs<Transform>(true)!);
        _tests.Add(entry);

        if (IsActiveInHierarchy)
        {
            RebuildUi();
            Relayout();
        }
    }

    public void RegisterSubLabel(SceneNode testRoot, Transform target, string text, float heightOffset = 2.8f)
    {
        _subLabelDefs.Add(new SubLabelDefinition(testRoot, target, text, heightOffset));
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        _labelFont ??= FontGlyphSet.LoadDefaultFont();

        _customUi = GetSiblingComponent<CustomUIComponent>(createIfNotExist: true);
        if (_customUi is not null)
        {
            _customUi.Name = "Math Intersections Test Controls";
            RebuildUi();
        }

        Relayout();
    }

    protected override void OnComponentDeactivated()
    {
        _customUi?.ClearFields();
        _customUi = null;
        DestroyLabels();
        DestroySubLabels();
        base.OnComponentDeactivated();
    }

    private void RebuildUi()
    {
        if (_customUi is null)
            return;

        _customUi.ClearFields();

        foreach (MathIntersectionsWorldTestEntry entry in _tests)
        {
            MathIntersectionsWorldTestEntry capturedEntry = entry;
            _customUi.AddBoolField(
                capturedEntry.DisplayName,
                () => capturedEntry.RootNode.IsActiveSelf,
                value => SetTestEnabled(capturedEntry, value),
                $"Toggle the {capturedEntry.DisplayName} math intersection test.");
        }
    }

    private void SetTestEnabled(MathIntersectionsWorldTestEntry entry, bool enabled)
    {
        entry.RootNode.IsActiveSelf = enabled;
        Relayout();
    }

    private void Relayout()
    {
        if (_tests.Count == 0)
            return;

        List<MathIntersectionsWorldTestEntry> activeTests = [];
        foreach (MathIntersectionsWorldTestEntry entry in _tests)
        {
            if (entry.RootNode.IsActiveSelf)
                activeTests.Add(entry);
        }

        if (activeTests.Count == 0)
            return;

        if (activeTests.Count == 1)
        {
            CenterAtOrigin(activeTests[0]);
            UpdateLabelPlacement(activeTests[0]);
            return;
        }

        float cellWidth = 0.0f;
        float cellDepth = 0.0f;
        foreach (MathIntersectionsWorldTestEntry entry in activeTests)
        {
            Vector3 size = entry.Bounds.Size;
            cellWidth = MathF.Max(cellWidth, size.X);
            cellDepth = MathF.Max(cellDepth, size.Z);
        }

        const float cellPadding = 4.0f;
        cellWidth += cellPadding;
        cellDepth += cellPadding;

        int columns = (int)MathF.Ceiling(MathF.Sqrt(activeTests.Count));
        int rows = (int)MathF.Ceiling(activeTests.Count / (float)columns);
        float startX = -((columns - 1) * cellWidth) * 0.5f;
        float startZ = -((rows - 1) * cellDepth) * 0.5f;

        for (int index = 0; index < activeTests.Count; index++)
        {
            int row = index / columns;
            int column = index % columns;
            Vector3 desiredCenter = new(
                startX + column * cellWidth,
                0.0f,
                startZ + row * cellDepth);

            CenterWithinCell(activeTests[index], desiredCenter);
            UpdateLabelPlacement(activeTests[index]);
        }
    }

    private static void CenterAtOrigin(MathIntersectionsWorldTestEntry entry)
        => CenterWithinCell(entry, Vector3.Zero);

    private static void CenterWithinCell(MathIntersectionsWorldTestEntry entry, Vector3 desiredCenter)
    {
        Vector3 boundsCenter = entry.Bounds.Center;
        entry.RootTransform.Translation = new Vector3(
            desiredCenter.X - boundsCenter.X,
            0.0f,
            desiredCenter.Z - boundsCenter.Z);
    }

    private void RenderLabels()
    {
        foreach (MathIntersectionsWorldTestEntry entry in _tests)
        {
            if (!entry.RootNode.IsActiveSelf)
                continue;

            MathIntersectionsWorldLabelSet labels = EnsureLabels(entry);
            UpdateLabelPlacement(entry);
            labels.Render();
        }

        foreach (SubLabelDefinition def in _subLabelDefs)
        {
            if (!def.TestRoot.IsActiveSelf)
                continue;

            SubLabelRenderable subLabel = EnsureSubLabel(def);
            subLabel.Render();
        }
    }

    private MathIntersectionsWorldLabelSet EnsureLabels(MathIntersectionsWorldTestEntry entry)
    {
        if (_labels.TryGetValue(entry, out MathIntersectionsWorldLabelSet? labels))
            return labels;

        FontGlyphSet font = _labelFont ??= FontGlyphSet.LoadDefaultFont();
        float titleWidth = font.MeasureString(entry.DisplayName, TitleFontSize).X;
        float descriptionWidth = font.MeasureString(entry.Description, DescriptionFontSize).X;
        float blockWidth = MathF.Max(titleWidth, descriptionWidth);

        labels = new MathIntersectionsWorldLabelSet(
            CreateLabelText(entry.RootTransform, font, entry.DisplayName, TitleFontSize, LabelScale * OutlineScaleMultiplier, ColorF4.Black, blockWidth, TitleLineYOffset),
            CreateLabelText(entry.RootTransform, font, entry.DisplayName, TitleFontSize, LabelScale, ColorF4.White, blockWidth, TitleLineYOffset),
            CreateLabelText(entry.RootTransform, font, entry.Description, DescriptionFontSize, LabelScale * OutlineScaleMultiplier, ColorF4.Black, blockWidth, DescriptionLineYOffset),
            CreateLabelText(entry.RootTransform, font, entry.Description, DescriptionFontSize, LabelScale, new ColorF4(0.85f, 0.85f, 0.85f, 1.0f), blockWidth, DescriptionLineYOffset));

        _labels.Add(entry, labels);
        return labels;
    }

    private SubLabelRenderable EnsureSubLabel(SubLabelDefinition def)
    {
        if (_subLabels.TryGetValue(def, out SubLabelRenderable? subLabel))
            return subLabel;

        FontGlyphSet font = _labelFont ??= FontGlyphSet.LoadDefaultFont();
        subLabel = new SubLabelRenderable(
            CreateSubLabelText(def.Target, font, def.Text, SubLabelScale * OutlineScaleMultiplier, ColorF4.Black, def.HeightOffset),
            CreateSubLabelText(def.Target, font, def.Text, SubLabelScale, new ColorF4(0.9f, 0.9f, 0.8f, 1.0f), def.HeightOffset));

        _subLabels.Add(def, subLabel);
        return subLabel;
    }

    private static UIText CreateLabelText(Transform textTransform, FontGlyphSet font, string content, float fontSize, float scale, ColorF4 color, float blockWidth, float yOffset)
    {
        var text = new UIText
        {
            TextTransform = textTransform,
            Text = content,
            Font = font,
            FontSize = fontSize,
            Scale = scale,
            Color = color,
            RenderPass = (int)EDefaultRenderPass.OnTopForward,
        };

        PositionText(text, font, content, fontSize, blockWidth, yOffset);
        return text;
    }

    private static UIText CreateSubLabelText(Transform textTransform, FontGlyphSet font, string content, float scale, ColorF4 color, float heightOffset)
    {
        var text = new UIText
        {
            TextTransform = textTransform,
            Text = content,
            Font = font,
            FontSize = SubLabelFontSize,
            Scale = scale,
            Color = color,
            RenderPass = (int)EDefaultRenderPass.OnTopForward,
            LocalTranslation = new Vector3(0.0f, heightOffset, 0.0f),
        };

        float width = font.MeasureString(content, SubLabelFontSize).X;
        PositionText(text, font, content, SubLabelFontSize, width, 0.0f);
        return text;
    }

    private static void PositionText(UIText text, FontGlyphSet font, string content, float fontSize, float blockWidth, float yOffset)
    {
        Dictionary<int, (Vector2 translation, Vector2 scale, float rotation)> glyphOffsets = [];
        Vector2 translation = new(-blockWidth * 0.5f, yOffset);
        for (int i = 0; i < content.Length; i++)
            glyphOffsets[i] = (translation, Vector2.One, 0.0f);

        text.GlyphRelativeTransforms = glyphOffsets;
    }

    private void UpdateLabelPlacement(MathIntersectionsWorldTestEntry entry)
    {
        if (!_labels.TryGetValue(entry, out MathIntersectionsWorldLabelSet? labels))
            return;

        Vector3 labelPosition = new(
            entry.Bounds.Center.X,
            entry.Bounds.Max.Y + LabelHeightOffset,
            entry.Bounds.Center.Z);

        labels.TitleBackdrop.LocalTranslation = labelPosition;
        labels.TitleFace.LocalTranslation = labelPosition;
        labels.DescriptionBackdrop.LocalTranslation = labelPosition;
        labels.DescriptionFace.LocalTranslation = labelPosition;
    }

    private void DestroyLabels()
    {
        foreach (MathIntersectionsWorldLabelSet labels in _labels.Values)
            labels.Destroy();

        _labels.Clear();
    }

    private void DestroySubLabels()
    {
        foreach (SubLabelRenderable subLabel in _subLabels.Values)
            subLabel.Destroy();

        _subLabels.Clear();
    }

    private sealed record MathIntersectionsWorldTestEntry(SceneNode RootNode, string DisplayName, string Description, AABB Bounds, Transform RootTransform);

    private sealed record SubLabelDefinition(SceneNode TestRoot, Transform Target, string Text, float HeightOffset);

    private sealed record SubLabelRenderable(UIText Backdrop, UIText Face)
    {
        public void Render()
        {
            Backdrop.Render();
            Face.Render();
        }

        public void Destroy()
        {
            if (Backdrop.Mesh is not null)
                Backdrop.Mesh.Destroy();
            if (Face.Mesh is not null)
                Face.Mesh.Destroy();
        }
    }

    private sealed record MathIntersectionsWorldLabelSet(UIText TitleBackdrop, UIText TitleFace, UIText DescriptionBackdrop, UIText DescriptionFace)
    {
        public void Render()
        {
            TitleBackdrop.Render();
            DescriptionBackdrop.Render();
            TitleFace.Render();
            DescriptionFace.Render();
        }

        public void Destroy()
        {
            DestroyText(TitleBackdrop);
            DestroyText(TitleFace);
            DestroyText(DescriptionBackdrop);
            DestroyText(DescriptionFace);
        }

        private static void DestroyText(UIText text)
        {
            if (text.Mesh is not null)
                text.Mesh.Destroy();
        }
    }
}