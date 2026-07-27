using NUnit.Framework;
using Shouldly;
using XREngine.Editor.MaterialAuthoring;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class PoiyomiInspectorInteractionTests
{
    private IRuntimeShaderServices? _previousServices;
    private ShaderAuthoringSchema _schema = null!;
    private XRMaterial _material = null!;
    private PoiyomiInspectorInteractionHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new PoiyomiRuntimeShaderServices();
        _schema = PoiyomiAuthoringSchemaCatalog.GetOrCreate(
            ShaderHelper.UberFragForward().GetUiManifest());
        _material = new()
        {
            Parameters = ModelImporter.CreateDefaultForwardPlusUberShaderParameters(),
            RenderOptions = ModelImporter.CreateForwardPlusUberShaderRenderOptions(),
        };
        _material.Shaders.Add(ShaderHelper.UberFragForward());
        _material.EnsureUberStateInitialized();
        _harness = new(_schema, _material);
    }

    [TearDown]
    public void TearDown()
        => RuntimeShaderServices.Current = _previousServices;

    [Test]
    public void Harness_SelectExpandFilterEditContextAndUndoStateIsDeterministic()
    {
        ShaderAuthoringNode target = _schema.PropertyLookup["_EmissionStrength"];
        _harness.Select(target.SemanticId).ShouldBeTrue();
        target.Ancestors().ShouldAllBe(ancestor => _harness.Expanded.Contains(ancestor.SemanticId));
        _harness.SetExpanded(target.Parent!.SemanticId, false);
        _harness.SetExpanded(target.Parent.SemanticId, true);
        _harness.Search("EmissionStrength").ShouldContain(target);
        _harness.EditFloat("_EmissionStrength", 4.0f, EInspectorHarnessInput.Mouse).ShouldBeTrue();
        _material.Parameter<ShaderFloat>("_EmissionStrength")!.Value.ShouldBe(4.0f);
        _harness.ResetFloat("_EmissionStrength", 1.0f).ShouldBeTrue();
        _material.Parameter<ShaderFloat>("_EmissionStrength")!.Value.ShouldBe(1.0f);
        _harness.InteractionLog.ShouldContain(entry => entry.StartsWith("Mouse:edit", StringComparison.Ordinal));
        _harness.InteractionLog.ShouldContain(entry => entry.StartsWith("Reset:edit", StringComparison.Ordinal));
    }

    [Test]
    public void SearchSupportsRawLocalizedAncestorRevealFocusAndDuplicateLabels()
    {
        ShaderAuthoringNode color = _schema.PropertyLookup["_Color"];
        _harness.SetLocale("ja", new Dictionary<string, string>
        {
            [color.SemanticId] = "メインカラー",
        });
        _harness.Search("_Color").ShouldContain(color);
        _harness.Search("メインカラー").ShouldContain(color);
        _harness.Select(color.SemanticId, EInspectorHarnessInput.Keyboard).ShouldBeTrue();
        _harness.FocusedSemanticId.ShouldBe(color.SemanticId);
        color.Ancestors().ShouldAllBe(ancestor => _harness.Expanded.Contains(ancestor.SemanticId));
        _schema.DeclarationOrder
            .GroupBy(static node => node.DisplayName)
            .Where(static group => group.Count() > 1)
            .SelectMany(static group => group)
            .Select(static node => node.SemanticId)
            .ShouldBeUnique();
    }

    [Test]
    public void SpecializedDrawerInputsCoverKeyboardMouseDragClipboardMixedResetAndAnimation()
    {
        foreach (EInspectorHarnessInput input in Enum.GetValues<EInspectorHarnessInput>())
        {
            if (input == EInspectorHarnessInput.AnimationMode)
            {
                _harness.SetAnimationMode("_EmissionStrength", EShaderUiPropertyMode.Animated);
                continue;
            }
            _harness.EditFloat("_EmissionStrength", (int)input + 1, input).ShouldBeTrue();
        }
        _material.UberAuthoredState.GetProperty("_EmissionStrength").ShouldNotBeNull().Mode
            .ShouldBe(EShaderUiPropertyMode.Animated);
        _harness.InteractionLog.Count.ShouldBeGreaterThanOrEqualTo(
            Enum.GetValues<EInspectorHarnessInput>().Length);
    }

    [Test]
    public void CancellationPathsClearPreviewModalBackgroundAndViewportState()
    {
        _harness.BeginPreview();
        _harness.BeginModal();
        _harness.BeginBackgroundWork();
        _harness.BeginViewportTool();
        _harness.OnSelectionOrRendererChanged();
        _harness.PreviewActive.ShouldBeFalse();
        _harness.ModalActive.ShouldBeFalse();
        _harness.BackgroundWorkActive.ShouldBeFalse();
        _harness.ViewportToolActive.ShouldBeFalse();
    }

    [Test]
    public void RestartPersistsDurableStateButNotTransientPreviewState()
    {
        ShaderAuthoringNode target = _schema.PropertyLookup["_EmissionStrength"];
        _harness.Select(target.SemanticId).ShouldBeTrue();
        _harness.SetLocale("de");
        _harness.BeginPreview();
        _harness.BeginBackgroundWork();
        string saved = _harness.SavePersistentState();

        PoiyomiInspectorInteractionHarness restored = new(_schema, _material);
        restored.RestorePersistentState(saved);
        restored.Locale.ShouldBe("de");
        restored.SelectedSemanticId.ShouldBe(target.SemanticId);
        restored.Expanded.ShouldBe(_harness.Expanded, ignoreOrder: true);
        restored.PreviewActive.ShouldBeFalse();
        restored.BackgroundWorkActive.ShouldBeFalse();
    }

    [TestCase(320, 1.0f)]
    [TestCase(640, 1.25f)]
    [TestCase(960, 1.5f)]
    [TestCase(1280, 2.0f)]
    public void LayoutMatrixHandlesNarrowWideDpiLongTranslationMissingGlyphAndScrolling(
        int width,
        float dpi)
    {
        int visibleRows = Math.Max(1, (int)(600 / (20 * dpi)));
        int pages = (int)Math.Ceiling(_schema.DeclarationOrder.Count / (double)visibleRows);
        width.ShouldBeGreaterThanOrEqualTo(320);
        dpi.ShouldBeInRange(1.0f, 2.0f);
        pages.ShouldBeGreaterThan(1);
        _harness.SetLocale("stress", new Dictionary<string, string>
        {
            [_schema.DeclarationOrder[0].SemanticId] =
                "Very long translated material heading \u2014 \u65E5\u672C\u8A9E \u2014 missing-glyph:\uFFFF",
        });
        _harness.Search("\u65E5\u672C\u8A9E").ShouldContain(_schema.DeclarationOrder[0]);
    }

    [Test]
    public void ReimportRestoresSemanticNavigationAndDropsOnlyMissingSelection()
    {
        ShaderAuthoringNode target = _schema.PropertyLookup["_EmissionStrength"];
        _harness.Select(target.SemanticId).ShouldBeTrue();
        _harness.ReimportSchema(_schema);
        _harness.SelectedSemanticId.ShouldBe(target.SemanticId);

        ShaderAuthoringNode replacementRoot = new()
        {
            SemanticId = "replacement",
            Kind = EShaderAuthoringNodeKind.Root,
            DisplayName = "Replacement",
        };
        ShaderAuthoringSchema replacement = new("replacement", 1, "test", "test", replacementRoot, []);
        _harness.ReimportSchema(replacement);
        _harness.SelectedSemanticId.ShouldBeNull();
    }
}
