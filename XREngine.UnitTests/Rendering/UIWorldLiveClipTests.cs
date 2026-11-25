using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIWorldLiveClipTests
{
    [Test]
    public void LiveDirectWorldDrawUsesNestedCropMaterial()
    {
        using UITestShaderScope shaders = new();
        using IDisposable snippets = shaders.RegisterCanonicalCropSnippets();
        using IDisposable materialTarget =
            RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
                EngineMaterialConstructionTarget.DesktopGlsl);
        LiveClipScene scene = CreateScene();
        try
        {
            XRMaterial clip = scene.Clipped.LiveWorldClipMaterial.ShouldNotBeNull();
            scene.Clipped.RenderCommand3D.MaterialOverride.ShouldBeSameAs(clip);
            scene.Clipped.HasAuthoredWorldMaterialOverride.ShouldBeFalse();
            scene.Free.LiveWorldClipMaterial.ShouldBeNull();
            scene.Free.RenderCommand3D.MaterialOverride.ShouldBeNull();

            XRMaterial source = scene.Clipped.Mesh!.Material!;
            clip.ShouldNotBeSameAs(source);
            clip.Parameters.Length.ShouldBe(0);
            clip.Textures.Count.ShouldBe(source.Textures.Count);
            clip.RenderOptions.ShouldBeSameAs(source.RenderOptions);
            clip.Shaders.Count.ShouldBe(source.Shaders.Count);
            clip.Shaders.Count(UIWorldCropShaderContract.IsCanonicalClippedFragment).ShouldBe(1);

            BoundingRectangle crop = UIClipRegion.ResolveCrop(scene.Clipped.BoundableTransform, false)
                .ShouldNotBeNull();
            scene.Clipped.LiveWorldClipMinMax.ShouldBe(new Vector4(crop.MinX, crop.MinY, crop.MaxX, crop.MaxY));
        }
        finally
        {
            scene.Root.Destroy(true);
        }
    }

    [Test]
    public void DirectWorldUiMaterialsEnterTheAdvancedLatePasses()
    {
        using UITestShaderScope shaders = new();
        using IDisposable snippets = shaders.RegisterCanonicalCropSnippets();
        using IDisposable materialTarget =
            RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
                EngineMaterialConstructionTarget.DesktopGlsl);
        LiveClipScene scene = CreateScene();
        try
        {
            XRMaterial free = scene.Free.Mesh!.Material!;
            XRMaterial source = scene.Clipped.Mesh!.Material!;
            XRMaterial clip = scene.Clipped.LiveWorldClipMaterial.ShouldNotBeNull();
            free.AdvancedLatePassMetadata.ShouldNotBeNull().Kind.ShouldBe(EAdvancedLatePassKind.UserInterface);
            clip.AdvancedLatePassMetadata.ShouldBeSameAs(source.AdvancedLatePassMetadata);
            foreach (XRMaterial material in new[] { free, clip })
            {
                AdvancedLatePassEligibilityValidator.TryValidateLatePass(
                    material, (int)EDefaultRenderPass.TransparentForward, out string? reason).ShouldBeTrue(reason);
                AdvancedLatePassEligibilityValidator.TryValidateLatePass(
                    material, (int)EDefaultRenderPass.OnTopForward, out reason).ShouldBeTrue(reason);
            }

            XRMaterial unclassified = new() { TransparencyMode = ETransparencyMode.Opaque };
            AdvancedLatePassEligibilityValidator.TryValidateLatePass(
                unclassified, (int)EDefaultRenderPass.TransparentForward, out _).ShouldBeFalse();
        }
        finally
        {
            scene.Root.Destroy(true);
        }
    }

    [Test]
    public void LiveWorldClipFollowsAncestorClipAndRegion()
    {
        using UITestShaderScope shaders = new();
        using IDisposable snippets = shaders.RegisterCanonicalCropSnippets();
        using IDisposable materialTarget =
            RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
                EngineMaterialConstructionTarget.DesktopGlsl);
        LiveClipScene scene = CreateScene();
        try
        {
            Vector4 before = scene.Clipped.LiveWorldClipMinMax;

            Apply(scene.Outer, new Vector2(60), new Vector2(20));
            scene.Canvas.RecalculateMatrixHierarchyImmediate();
            scene.Outer.RefreshAxisAlignedRegion();
            scene.Clipped.BoundableTransform.RefreshAxisAlignedRegion();
            BoundingRectangle moved = UIClipRegion.ResolveCrop(scene.Clipped.BoundableTransform, false)
                .ShouldNotBeNull();
            scene.Clipped.LiveWorldClipMinMax.ShouldBe(new Vector4(moved.MinX, moved.MinY, moved.MaxX, moved.MaxY));
            scene.Clipped.LiveWorldClipMinMax.ShouldNotBe(before);

            scene.OuterClip.ClipToBounds = false;
            scene.Clipped.LiveWorldClipMaterial.ShouldBeNull();
            scene.Clipped.RenderCommand3D.MaterialOverride.ShouldBeNull();

            scene.OuterClip.ClipToBounds = true;
            scene.Clipped.RenderCommand3D.MaterialOverride.ShouldBeSameAs(
                scene.Clipped.LiveWorldClipMaterial.ShouldNotBeNull());
        }
        finally
        {
            scene.Root.Destroy(true);
        }
    }

    [Test]
    public void AuthoredWorldOverrideKeepsUnclippedDraw()
    {
        using UITestShaderScope shaders = new();
        using IDisposable snippets = shaders.RegisterCanonicalCropSnippets();
        using IDisposable materialTarget =
            RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
                EngineMaterialConstructionTarget.DesktopGlsl);
        LiveClipScene scene = CreateScene();
        try
        {
            XRMaterial authored = new();
            scene.Clipped.RenderCommand3D.MaterialOverride = authored;
            scene.Clipped.RefreshLiveWorldClip();

            scene.Clipped.HasAuthoredWorldMaterialOverride.ShouldBeTrue();
            scene.Clipped.LiveWorldClipMaterial.ShouldBeNull();
            scene.Clipped.RenderCommand3D.MaterialOverride.ShouldBeSameAs(authored);

            scene.Clipped.RenderCommand3D.MaterialOverride = null;
            scene.Clipped.RefreshLiveWorldClip();
            scene.Clipped.RenderCommand3D.MaterialOverride.ShouldBeSameAs(
                scene.Clipped.LiveWorldClipMaterial.ShouldNotBeNull());
        }
        finally
        {
            scene.Root.Destroy(true);
        }
    }

    [Test]
    public void DeactivationRemovesLiveClipMaterial()
    {
        using UITestShaderScope shaders = new();
        using IDisposable snippets = shaders.RegisterCanonicalCropSnippets();
        using IDisposable materialTarget =
            RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
                EngineMaterialConstructionTarget.DesktopGlsl);
        LiveClipScene scene = CreateScene();
        try
        {
            scene.Clipped.LiveWorldClipMaterial.ShouldNotBeNull();
            scene.Clipped.SceneNode!.IsActiveSelf = false;

            scene.Clipped.LiveWorldClipMaterial.ShouldBeNull();
            scene.Clipped.RenderCommand3D.MaterialOverride.ShouldBeNull();
        }
        finally
        {
            scene.Root.Destroy(true);
        }
    }

    private sealed record LiveClipScene(SceneNode Root, UICanvasTransform Canvas, UIBoundableTransform Outer,
        UIPublicationMutationProbe OuterClip, UIMaterialComponent Clipped, UIMaterialComponent Free);

    private static LiveClipScene CreateScene()
    {
        UICanvasTransform canvasTransform = new()
        {
            Width = 100,
            Height = 100,
            DrawSpace = ECanvasDrawSpace.World,
        };
        SceneNode root = new("World live clip", canvasTransform)
        {
            IsActiveSelf = false,
        };
        root.SetWorldContext(new TestWorldContext());
        UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
        canvas.PreferOffscreenRenderingForNonScreenSpaces = false;

        SceneNode outerNode = new(root) { Name = "Outer clip" };
        UIBoundableTransform outer = outerNode.SetTransform<UIBoundableTransform>();
        UIPublicationMutationProbe outerClip = outerNode.AddComponent<UIPublicationMutationProbe>()!;
        outerClip.ClipToBounds = true;
        UIMaterialComponent clipped = Quad(outerNode, "Clipped");
        UIMaterialComponent free = Quad(root, "Free");

        root.IsActiveSelf = true;
        Apply(canvasTransform, new Vector2(100), Vector2.Zero);
        Apply(outer, new Vector2(40), new Vector2(10));
        Apply(clipped.BoundableTransform, new Vector2(60), Vector2.Zero);
        Apply(free.BoundableTransform, new Vector2(20), new Vector2(70));
        canvasTransform.RecalculateMatrixHierarchyImmediate();
        outer.RefreshAxisAlignedRegion();
        clipped.BoundableTransform.RefreshAxisAlignedRegion();
        free.BoundableTransform.RefreshAxisAlignedRegion();
        return new LiveClipScene(root, canvasTransform, outer, outerClip, clipped, free);
    }

    private static UIMaterialComponent Quad(SceneNode parent, string name)
    {
        SceneNode node = new(parent) { Name = name };
        node.SetTransform<UIBoundableTransform>();
        UIMaterialComponent component = node.AddComponent<UIMaterialComponent>()!;
        component.RenderInfo3D.IsVisible = true;
        component.RenderInfo3D.CastsShadows = false;
        component.RenderPass = (int)EDefaultRenderPass.TransparentForward;
        // A default quad has no mesh until its material is read. Read it, so that the live draw exists.
        component.Material.ShouldNotBeNull();
        component.Mesh.ShouldNotBeNull();
        return component;
    }

    private static void Apply(UIBoundableTransform transform, Vector2 size, Vector2 translation)
    {
        transform.ApplyCalculatedLayout(new UILayoutNodeOutput
        {
            ActualSize = size,
            LocalTranslation = translation,
            Visible = true,
        }, Vector2.Zero);
        transform.RefreshAxisAlignedRegion();
    }
}
