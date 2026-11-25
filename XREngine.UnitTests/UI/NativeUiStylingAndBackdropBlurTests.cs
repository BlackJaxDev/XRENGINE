using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Pipelines.Validation;
using XREngine.Rendering.Resources;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.UI;

[TestFixture]
public sealed class NativeUiStylingAndBackdropBlurTests
{
    private EUiBackdropBlurFormat _savedBlurFormat;

    [SetUp]
    public void SetUp()
    {
        _savedBlurFormat = RuntimeEngine.Rendering.Settings.UiBackdropBlurFormat;
        RuntimeEngine.Rendering.Settings.UiBackdropBlurFormat = EUiBackdropBlurFormat.Rgba8;
    }

    [TearDown]
    public void TearDown()
        => RuntimeEngine.Rendering.Settings.UiBackdropBlurFormat = _savedBlurFormat;

    [Test]
    public void UIListTransform_ArrangeAndLegacyResize_InsetChildrenByContainerPadding()
    {
        SceneNode root = UITestCanvasLayout.CreateCanvasRoot("ListCanvas", 360.0f, 260.0f, out UICanvasTransform canvas);
        SceneNode listNode = new(root, "ListRoot");
        UIListTransform listTransform = listNode.SetTransform<UIListTransform>();
        listTransform.MinAnchor = Vector2.Zero;
        listTransform.MaxAnchor = Vector2.Zero;
        listTransform.NormalizedPivot = Vector2.Zero;
        listTransform.Width = 360.0f;
        listTransform.Height = 260.0f;
        listTransform.Padding = new Vector4(24.0f, 20.0f, 24.0f, 16.0f);
        listTransform.ItemSpacing = 12.0f;
        listTransform.DisplayHorizontal = false;
        listTransform.ItemAlignment = EListAlignment.TopOrLeft;

        SceneNode childA = new(listNode, "ChildA");
        UIBoundableTransform childTransformA = childA.SetTransform<UIBoundableTransform>();
        childTransformA.Width = 200.0f;
        childTransformA.Height = 40.0f;

        SceneNode childB = new(listNode, "ChildB");
        UIBoundableTransform childTransformB = childB.SetTransform<UIBoundableTransform>();
        childTransformB.Width = 200.0f;
        childTransformB.Height = 44.0f;

        UITestCanvasLayout.Settle(canvas);

        UIListTransform.UIListChildPlacementInfo placementA = childTransformA.PlacementInfo.ShouldBeOfType<UIListTransform.UIListChildPlacementInfo>();
        UIListTransform.UIListChildPlacementInfo placementB = childTransformB.PlacementInfo.ShouldBeOfType<UIListTransform.UIListChildPlacementInfo>();

        // Worker layout keeps the left padding (24 px) in the child translation and the flow
        // offset in the placement. The top padding (16 px) insets the first row from 260.
        float crossA = placementA.GetRelativeItemMatrix().Translation.X + childTransformA.ActualLocalBottomLeftTranslation.X;
        float crossB = placementB.GetRelativeItemMatrix().Translation.X + childTransformB.ActualLocalBottomLeftTranslation.X;
        crossA.ShouldBe(24.0f, 0.01f);
        crossB.ShouldBe(24.0f, 0.01f);
        placementA.BottomOrLeftOffset.ShouldBe(260.0f - 16.0f - 40.0f, 0.01f);
        placementB.BottomOrLeftOffset.ShouldBe(204.0f - 12.0f - 44.0f, 0.01f);
    }

    [Test]
    public void UIBoundableTransform_VisualProperties_DoNotInvalidateLayoutAndMultiplyOpacity()
    {
        SceneNode parentNode = UITestCanvasLayout.CreateCanvasRoot("Parent", 300.0f, 200.0f, out UICanvasTransform parentTransform);

        SceneNode childNode = new(parentNode, "Child");
        UIBoundableTransform childTransform = childNode.SetTransform<UIBoundableTransform>();
        childTransform.Width = 100.0f;
        childTransform.Height = 50.0f;

        UITestCanvasLayout.Settle(parentTransform);
        BoundingRectangleF layoutBoundsBefore = childTransform.GetActualBounds();

        parentTransform.Opacity = 0.5f;
        childTransform.Opacity = 0.6f;
        childTransform.VisualScale = new Vector2(1.1f, 0.9f);
        childTransform.VisualOffset = new Vector2(8.0f, -6.0f);

        childTransform.GetActualBounds().ShouldBe(layoutBoundsBefore);
        childTransform.EffectiveOpacity.ShouldBe(0.3f, 0.0001f);
    }

    [Test]
    public void UIStyleProperties_ResolveOverAndPackFlagsCorrectly()
    {
        UIStyleProperties baseProps = new()
        {
            BackgroundColor = new ColorF4(0.1f, 0.2f, 0.3f, 0.8f),
            GradientBottomColor = new ColorF4(0.05f, 0.1f, 0.15f, 0.9f),
            CornerRadii = new Vector4(22.0f),
            CornerExponent = 2.8f,
            BorderWidth = 1.0f,
            BorderColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.16f),
            BorderHighlightBoost = 0.18f,
            ShadowColor = new ColorF4(0.0f, 0.0f, 0.0f, 0.48f),
            ShadowParams = new Vector4(0.0f, 18.0f, 36.0f, -6.0f),
            BackdropBlurRadius = 28.0f,
            DecodeSrgbColors = true,
        };

        UIStyleProperties hoverOverride = new()
        {
            BackgroundColor = new ColorF4(0.2f, 0.5f, 1.0f, 1.0f),
            BackdropBlurRadius = 0.0f,
        };

        UIQuadStyle baseQuad = baseProps.ToQuadStyle();
        (baseQuad.Flags & EUIQuadStyleFlags.StyledBox).ShouldNotBe(EUIQuadStyleFlags.None);
        (baseQuad.Flags & EUIQuadStyleFlags.DecodeSrgbColors).ShouldNotBe(EUIQuadStyleFlags.None);
        (baseQuad.Flags & EUIQuadStyleFlags.VerticalGradient).ShouldNotBe(EUIQuadStyleFlags.None);
        (baseQuad.Flags & EUIQuadStyleFlags.DropShadow).ShouldNotBe(EUIQuadStyleFlags.None);
        (baseQuad.Flags & EUIQuadStyleFlags.BackdropBlur).ShouldNotBe(EUIQuadStyleFlags.None);
        baseQuad.BackdropBlurParams.X.ShouldBe(28.0f, 0.01f);
        baseQuad.BackdropBlurParams.Y.ShouldBe(1.0f, 0.01f);

        UIQuadStyle faded = baseQuad.WithOpacity(0.5f);
        faded.BorderColor.W.ShouldBe(0.08f, 0.001f);
        faded.ShadowColor.W.ShouldBe(0.24f, 0.001f);
        faded.BackdropBlurParams.Y.ShouldBe(0.5f, 0.001f);

        UIStyleProperties resolved = hoverOverride.ResolveOver(in baseProps);
        UIQuadStyle hoverQuad = resolved.ToQuadStyle();
        hoverQuad.CornerRadii.ShouldBe(new Vector4(22.0f));
        (hoverQuad.Flags & EUIQuadStyleFlags.BackdropBlur).ShouldBe(EUIQuadStyleFlags.None);
    }

    [Test]
    public void UIStyleAnimatorComponent_TimedSpringAndInterruptibleReversal_ConvergeCleanly()
    {
        SceneNode node = new("AnimatedCard");
        UIBoundableTransform transform = node.SetTransform<UIBoundableTransform>();
        transform.Width = 360.0f;
        transform.Height = 272.0f;

        UIStyleAnimatorComponent animator = node.AddComponent<UIStyleAnimatorComponent>()!;

        UIStyleProperties closed = new()
        {
            Opacity = 0.0f,
            VisualScale = new Vector2(0.94f, 0.94f),
            VisualOffset = new Vector2(0.0f, -10.0f),
            BackdropBlurRadius = 0.0f,
        };
        UIStyleProperties open = new()
        {
            Opacity = 1.0f,
            VisualScale = Vector2.One,
            VisualOffset = Vector2.Zero,
            BackdropBlurRadius = 28.0f,
        };

        animator.ApplyImmediate(in closed);
        transform.Opacity.ShouldBe(0.0f, 0.0001f);
        transform.VisualScale.X.ShouldBe(0.94f, 0.0001f);

        bool completed = false;
        animator.TransitionTo(
            in open,
            UITransitionSpec.AppleEaseOut(0.22f),
            UITransitionSpec.Spring(420.0f, 32.0f),
            UITransitionSpec.AppleEaseOut(0.22f),
            onCompleted: () => completed = true);

        animator.IsAnimating.ShouldBeTrue();

        // Advance halfway, verify smooth intermediate progress, then reverse mid-flight.
        for (int i = 0; i < 6; i++)
            animator.Step(1.0f / 60.0f);

        float midOpacity = transform.Opacity;
        midOpacity.ShouldBeGreaterThan(0.15f);
        midOpacity.ShouldBeLessThan(0.99f);

        bool closeCompleted = false;
        animator.TransitionTo(
            in closed,
            UITransitionSpec.EaseOut(0.18f),
            UITransitionSpec.EaseOut(0.18f),
            UITransitionSpec.EaseOut(0.18f),
            onCompleted: () => closeCompleted = true);

        // Immediately after reversal, opacity must start from its live mid-flight value (no pop).
        transform.Opacity.ShouldBe(midOpacity, 0.02f);

        for (int i = 0; i < 30; i++)
            animator.Step(1.0f / 60.0f);

        completed.ShouldBeFalse();
        closeCompleted.ShouldBeTrue();
        animator.IsAnimating.ShouldBeFalse();
        transform.Opacity.ShouldBe(0.0f, 0.001f);
        transform.VisualScale.X.ShouldBe(0.94f, 0.001f);
    }

    [Test]
    public void UiBackdropBlurResources_DeclaresThreeMipMonoAndStereoLayouts_AndTogglesFormats()
    {
        AdvancedRenderPipeline pipeline = new();

        RenderPipelineResourceProfile monoProfile = RenderPipelineValidationContext.DefaultProfile with
        {
            DisplayWidth = 1920u,
            DisplayHeight = 1080u,
            Stereo = false,
            ViewCount = 1u,
        };

        RenderPipelineResourceLayoutBuilder monoBuilder = new(monoProfile);
        UiBackdropBlurResources.AddToLayout(monoBuilder, pipeline);
        RenderPipelineResourceLayout monoLayout = monoBuilder.Build(monoProfile);

        monoLayout.TryGet(UiBackdropBlurResources.OutputTextureName, out RenderPipelineResourceSpec? monoSpec).ShouldBeTrue();
        TextureSpec monoTex = monoSpec.ShouldBeOfType<TextureSpec>();
        monoTex.MipPolicy.MipLevelCount.ShouldBe(3u);
        monoTex.Layers.ShouldBe(1u);
        monoTex.StereoCompatible.ShouldBeFalse();
        monoTex.SizedInternalFormat.ShouldBe(ESizedInternalFormat.Rgba8);

        monoLayout.TryGet(UiBackdropBlurResources.Mip0FboName, out RenderPipelineResourceSpec? mip0Spec).ShouldBeTrue();
        mip0Spec.ShouldBeOfType<FrameBufferSpec>();
        monoLayout.TryGet(UiBackdropBlurResources.Mip1FboName, out RenderPipelineResourceSpec? mip1Spec).ShouldBeTrue();
        mip1Spec.ShouldBeOfType<FrameBufferSpec>();
        monoLayout.TryGet(UiBackdropBlurResources.Mip2FboName, out RenderPipelineResourceSpec? mip2Spec).ShouldBeTrue();
        mip2Spec.ShouldBeOfType<FrameBufferSpec>();

        // Switch runtime format to Rgba16f and confirm stereo multiview layout + feature bit update.
        ulong rgba8Bits = UiBackdropBlurResources.GetFormatFeatureBits(40);
        RuntimeEngine.Rendering.Settings.UiBackdropBlurFormat = EUiBackdropBlurFormat.Rgba16f;
        ulong rgba16fBits = UiBackdropBlurResources.GetFormatFeatureBits(40);
        rgba16fBits.ShouldNotBe(rgba8Bits);

        RenderPipelineResourceProfile stereoProfile = monoProfile with { Stereo = true, ViewCount = 2u };
        RenderPipelineResourceLayoutBuilder stereoBuilder = new(stereoProfile);
        UiBackdropBlurResources.AddToLayout(stereoBuilder, pipeline);
        RenderPipelineResourceLayout stereoLayout = stereoBuilder.Build(stereoProfile);

        stereoLayout.TryGet(UiBackdropBlurResources.OutputTextureName, out RenderPipelineResourceSpec? stereoSpec).ShouldBeTrue();
        TextureSpec stereoTex = stereoSpec.ShouldBeOfType<TextureSpec>();
        stereoTex.MipPolicy.MipLevelCount.ShouldBe(3u);
        stereoTex.Layers.ShouldBe(2u);
        stereoTex.StereoCompatible.ShouldBeTrue();
        stereoTex.SizedInternalFormat.ShouldBe(ESizedInternalFormat.Rgba16f);
    }

    [Test]
    public void HostPipelines_WirePrepareUiBackdropBlurBeforeScreenSpaceUi()
    {
        AssertPipelineWiresBackdropBlur(new AdvancedRenderPipeline());
        AssertPipelineWiresBackdropBlur(new DefaultRenderPipeline());
        AssertPipelineWiresBackdropBlur(new RvcRenderPipeline());
    }

    private static void AssertPipelineWiresBackdropBlur(RenderPipeline pipeline)
    {
        List<ViewportRenderCommand> flattened = [];
        FlattenCommands(pipeline.CommandChain, flattened);

        int blurIndex = flattened.FindIndex(static c => c is VPRC_PrepareUiBackdropBlur);
        int uiIndex = flattened.FindIndex(static c => c is VPRC_RenderScreenSpaceUI);

        blurIndex.ShouldBeGreaterThanOrEqualTo(0, $"{pipeline.GetType().Name} must include VPRC_PrepareUiBackdropBlur.");
        uiIndex.ShouldBeGreaterThan(blurIndex, $"{pipeline.GetType().Name} must run VPRC_PrepareUiBackdropBlur before VPRC_RenderScreenSpaceUI.");
    }

    private static void FlattenCommands(ViewportRenderCommandContainer? container, List<ViewportRenderCommand> output)
    {
        if (container is null)
            return;

        foreach (ViewportRenderCommand command in container.Commands)
        {
            output.Add(command);
            if (command is VPRC_IfElse ifElse)
            {
                FlattenCommands(ifElse.TrueCommands, output);
                FlattenCommands(ifElse.FalseCommands, output);
            }
        }
    }
}
