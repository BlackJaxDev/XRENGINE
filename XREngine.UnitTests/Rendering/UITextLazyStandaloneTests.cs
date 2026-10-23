using NUnit.Framework;
using XREngine.Components;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Data.Geometry;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UITextLazyStandaloneTests
{
    private UITestShaderScope _scope = null!;

    [SetUp] public void SetUp() => _scope = new UITestShaderScope();
    [TearDown] public void TearDown() => _scope.Dispose();

    private static FontGlyphSet CreateFont()
        => new()
        {
            Glyphs = new Dictionary<string, FontGlyphSet.Glyph>
            {
                ["A"] = new(new System.Numerics.Vector2(8), System.Numerics.Vector2.Zero),
            },
            Atlas = new XRTexture2D(8, 8, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba,
                EPixelType.UnsignedByte, true),
            LayoutEmSize = 8,
        };

    [Test]
    public void OrdinaryCanvasTextUsesNoPrivateRendererUntilStandaloneIsRequested()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 40 };
        SceneNode root = new("Text canvas", transform);
        root.SetWorldContext(new TestWorldContext());
        FontGlyphSet font = CreateFont();
        try
        {
            root.AddComponent<UICanvasComponent>();
            UITextComponent text = root.NewChild("Label").AddComponent<UITextComponent>()!;
            text.Font = font;
            text.FontSize = 8;
            text.Text = "A";

            text.SupportsBatchedRendering.ShouldBeTrue();
            text.Mesh.ShouldBeNull();
            text.Material.ShouldBeNull();
            text.RenderCommand2D.Mesh.ShouldBeNull();
            text.RenderCommand2D.Instances.ShouldBe(1u);

            text.DisableBatching = true;
            text.SupportsBatchedRendering.ShouldBeFalse();
            text.Text = "AA";
            text.Mesh.ShouldNotBeNull();
            text.Mesh.Buffers.Count.ShouldBeGreaterThan(0);
            XRMeshRenderer retired = text.Mesh;
            XRMesh? retiredGeometry = retired.Mesh;
            XRMaterial? retiredMaterial = retired.Material;
            XRDataBuffer retiredTransforms = retired.Buffers["GlyphTransformsBuffer"];
            XRDataBuffer retiredUvs = retired.Buffers["GlyphTexCoordsBuffer"];

            text.DisableBatching = false;
            UILayoutNodeOutput output = new() { Visible = true, ActualSize = new(100, 40) };
            UIRenderRecord record = text.CapturePublicationRenderRecord(in output);
            record.SupportsBatch.ShouldBeTrue();
            text.Mesh.ShouldBeNull();
            text.Material.ShouldBeNull();
            text.RenderCommand2D.Instances.ShouldBe(2u);
            if (RuntimeEngine.RenderThreadId == 0)
            {
                retired.IsDestroyed.ShouldBeTrue();
                retiredGeometry.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
                retiredMaterial.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
                retiredTransforms.IsDestroyed.ShouldBeTrue();
                retiredUvs.IsDestroyed.ShouldBeTrue();
            }
        }
        finally
        {
            root.Destroy(true);
            font.Atlas?.Destroy(true);
            font.Destroy(true);
        }
    }

    [Test]
    public void CustomAndDirectWorldTextKeepStandaloneRendering()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 40 };
        SceneNode root = new("World text canvas", transform);
        root.SetWorldContext(new TestWorldContext());
        FontGlyphSet font = CreateFont();
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UITextMaterialRefreshProbe custom = root.NewChild("Custom").AddComponent<UITextMaterialRefreshProbe>()!;
            custom.Font = font;
            custom.FontSize = 8;
            custom.Text = "A";
            custom.SupportsBatchedRendering.ShouldBeFalse();
            custom.Mesh.ShouldNotBeNull();

            UITextComponent plain = root.NewChild("Plain").AddComponent<UITextComponent>()!;
            plain.Font = font;
            plain.FontSize = 8;
            plain.Text = "A";
            plain.Mesh.ShouldBeNull();

            transform.DrawSpace = ECanvasDrawSpace.World;
            canvas.PreferOffscreenRenderingForNonScreenSpaces = false;
            UILayoutNodeOutput output = new() { Visible = true, ActualSize = new(100, 40) };
            UIRenderRecord direct = plain.CapturePublicationRenderRecord(in output);
            direct.SupportsBatch.ShouldBeFalse();
            plain.Mesh.ShouldNotBeNull();
        }
        finally
        {
            root.Destroy(true);
            font.Atlas?.Destroy(true);
            font.Destroy(true);
        }
    }

    [Test]
    public void AuthoredRendererIsNotReplacedOrRetiredByTextUpdates()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 40 };
        SceneNode root = new("Authored text canvas", transform);
        root.SetWorldContext(new TestWorldContext());
        FontGlyphSet font = CreateFont();
        XRMeshRenderer renderer = new(XRMesh.Create(VertexQuad.PosZ(1, true, 0, false)),
            new XRMaterial([new ShaderFloat(1, "Custom")],
                new XRShader(EShaderType.Fragment, "void main() {}")));
        try
        {
            root.AddComponent<UICanvasComponent>();
            UITextComponent text = root.NewChild("Authored").AddComponent<UITextComponent>()!;
            text.Mesh = renderer;
            text.Font = font;
            text.FontSize = 8;
            text.Text = "A";
            text.SupportsBatchedRendering.ShouldBeFalse();
            text.Mesh.ShouldBeSameAs(renderer);
            text.Text = "AA";
            text.Mesh.ShouldBeSameAs(renderer);
            UILayoutNodeOutput output = new() { Visible = true, ActualSize = new(100, 40) };
            text.CapturePublicationRenderRecord(in output);
            text.Mesh.ShouldBeSameAs(renderer);
        }
        finally
        {
            root.Destroy(true);
            renderer.Destroy(true);
            font.Atlas?.Destroy(true);
            font.Destroy(true);
        }
    }

    [Test]
    public void MaterialCallbackCanReplaceOwnedRendererDuringBatchRetirement()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 40 };
        SceneNode root = new("Reentrant text canvas", transform);
        root.SetWorldContext(new TestWorldContext());
        FontGlyphSet font = CreateFont();
        XRMeshRenderer replacement = new(XRMesh.Create(VertexQuad.PosZ(1, true, 0, false)),
            new XRMaterial([new ShaderFloat(1, "Custom")],
                new XRShader(EShaderType.Fragment, "void main() {}")));
        try
        {
            root.AddComponent<UICanvasComponent>();
            UITextComponent text = root.NewChild("Reentrant").AddComponent<UITextComponent>()!;
            text.DisableBatching = true;
            text.Font = font;
            text.FontSize = 8;
            text.Text = "A";
            XRMeshRenderer retired = text.Mesh.ShouldNotBeNull();
            XRDataBuffer transforms = retired.Buffers["GlyphTransformsBuffer"];
            bool replaced = false;
            text.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(UITextComponent.Material) || replaced || text.Mesh is not null)
                    return;
                replaced = true;
                text.Mesh = replacement;
            };
            text.DisableBatching = false;
            UILayoutNodeOutput output = new() { Visible = true, ActualSize = new(100, 40) };
            text.CapturePublicationRenderRecord(in output);
            replaced.ShouldBeTrue();
            text.Mesh.ShouldBeSameAs(replacement);
            replacement.IsDestroyed.ShouldBeFalse();
            text.SupportsBatchedRendering.ShouldBeFalse();
            if (RuntimeEngine.RenderThreadId == 0)
            {
                retired.IsDestroyed.ShouldBeTrue();
                transforms.IsDestroyed.ShouldBeTrue();
            }
        }
        finally
        {
            root.Destroy(true);
            replacement.Destroy(true);
            font.Atlas?.Destroy(true);
            font.Destroy(true);
        }
    }

    [Test]
    public void QueuedRetirementTransfersReattachedRendererToAuthor()
    {
        SceneNode root = new("Queued retirement", new UIBoundableTransform()) { IsActiveSelf = false };
        XRMesh geometry = XRMesh.Create(VertexQuad.PosZ(1, true, 0, false));
        XRMaterial material = new([new ShaderFloat(1, "Custom")],
            new XRShader(EShaderType.Fragment, "void main() {}"));
        XRMeshRenderer renderer = new(geometry, material);
        try
        {
            UITextComponent text = root.AddComponent<UITextComponent>()!;
            text.Mesh = renderer;
            text.Mesh = null;
            Action? queued = null;
            new UITextStandaloneRetirement(text, renderer, geometry, material, null, null, null,
                action => queued = action).Queue();
            queued.ShouldNotBeNull();
            text.Mesh = renderer;
            queued();
            text.Mesh.ShouldBeSameAs(renderer);
            renderer.IsDestroyed.ShouldBeFalse();
            geometry.IsDestroyed.ShouldBeFalse();
            material.IsDestroyed.ShouldBeFalse();
        }
        finally
        {
            root.Destroy(true);
            renderer.Destroy(true);
            geometry.Destroy(true);
            material.Destroy(true);
        }
    }

    [Test]
    public void DestroyingCallbackReattachmentDoesNotQueueAnUnguardedDestroy()
    {
        SceneNode root = new("Destroying reentry", new UIBoundableTransform()) { IsActiveSelf = false };
        XRMesh geometry = XRMesh.Create(VertexQuad.PosZ(1, true, 0, false));
        XRMaterial material = new([new ShaderFloat(1, "Custom")],
            new XRShader(EShaderType.Fragment, "void main() {}"));
        XRMeshRenderer renderer = new(geometry, material);
        try
        {
            UITextComponent text = root.AddComponent<UITextComponent>()!;
            // This authored callback runs before the retirement bundle's veto.
            renderer.Destroying += _ =>
            {
                text.Mesh = renderer;
                return true;
            };
            Action? queued = null;
            new UITextStandaloneRetirement(text, renderer, geometry, material, null, null, null,
                action => queued = action).Queue();
            queued.ShouldNotBeNull();
            queued();
            XRObjectBase.ProcessPendingDestructions();
            text.Mesh.ShouldBeSameAs(renderer);
            renderer.IsDestroyed.ShouldBeFalse();
            geometry.IsDestroyed.ShouldBeFalse();
            material.IsDestroyed.ShouldBeFalse();
        }
        finally
        {
            root.Destroy(true);
            renderer.Destroying = null;
            renderer.Destroy(true);
            geometry.Destroy(true);
            material.Destroy(true);
        }
    }

    [Test]
    public void PublicationLeaseDeferralVetoesDestroyAfterReattach()
    {
        int previousRenderThreadId = RuntimeEngine.RenderThreadId;
        RuntimeEngine.AssignRenderThread(0);
        UICanvasTransform transform = new() { Width = 100, Height = 40 };
        SceneNode root = new("Deferred retirement", transform);
        root.SetWorldContext(new TestWorldContext());
        FontGlyphSet font = CreateFont();
        XRMeshRenderer? renderer = null;
        XRMesh? geometry = null;
        XRMaterial? material = null;
        XRDataBuffer? transforms = null;
        bool leaseHeld = false;
        try
        {
            root.AddComponent<UICanvasComponent>();
            UITextComponent text = root.NewChild("Label").AddComponent<UITextComponent>()!;
            text.DisableBatching = true;
            text.Font = font;
            text.FontSize = 8;
            text.Text = "A";
            renderer = text.Mesh.ShouldNotBeNull();
            geometry = renderer.Mesh.ShouldNotBeNull();
            material = renderer.Material.ShouldNotBeNull();
            transforms = renderer.Buffers["GlyphTransformsBuffer"];
            renderer.EnterResourcePublicationLease();
            leaseHeld = true;
            text.DisableBatching = false;
            UILayoutNodeOutput output = new() { Visible = true, ActualSize = new(100, 40) };
            text.CapturePublicationRenderRecord(in output).SupportsBatch.ShouldBeTrue();
            renderer.IsDestroyed.ShouldBeFalse();
            text.Mesh = renderer;
            renderer.ExitResourcePublicationLease();
            leaseHeld = false;
            XRObjectBase.ProcessPendingDestructions();
            text.Mesh.ShouldBeSameAs(renderer);
            renderer.IsDestroyed.ShouldBeFalse();
            geometry.IsDestroyed.ShouldBeFalse();
            material.IsDestroyed.ShouldBeFalse();
            transforms.IsDestroyed.ShouldBeFalse();
        }
        finally
        {
            if (leaseHeld)
                renderer!.ExitResourcePublicationLease();
            root.Destroy(true);
            renderer?.Destroy(true);
            transforms?.Destroy(true);
            geometry?.Destroy(true);
            material?.Destroy(true);
            font.Atlas?.Destroy(true);
            font.Destroy(true);
            RuntimeEngine.AssignRenderThread(previousRenderThreadId);
        }
    }

    [Test]
    public void ClearingMeshExternallyRetiresOldBundleBeforeRecreation()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 40 };
        SceneNode root = new("Detached text renderer", transform);
        root.SetWorldContext(new TestWorldContext());
        FontGlyphSet font = CreateFont();
        try
        {
            root.AddComponent<UICanvasComponent>();
            UITextComponent text = root.NewChild("Label").AddComponent<UITextComponent>()!;
            text.DisableBatching = true;
            text.Font = font;
            text.FontSize = 8;
            text.Text = "A";
            XRMeshRenderer oldRenderer = text.Mesh.ShouldNotBeNull();
            XRDataBuffer oldTransforms = oldRenderer.Buffers["GlyphTransformsBuffer"];
            text.Mesh = null;
            text.Text = "AA";
            text.Mesh.ShouldNotBeNull().ShouldNotBeSameAs(oldRenderer);
            if (RuntimeEngine.RenderThreadId == 0)
            {
                oldRenderer.IsDestroyed.ShouldBeTrue();
                oldTransforms.IsDestroyed.ShouldBeTrue();
            }
        }
        finally
        {
            root.Destroy(true);
            font.Atlas?.Destroy(true);
            font.Destroy(true);
        }
    }
}
