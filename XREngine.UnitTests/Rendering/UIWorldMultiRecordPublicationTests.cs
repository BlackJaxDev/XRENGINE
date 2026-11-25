using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIWorldMultiRecordPublicationTests
{
    [Test]
    public void DirectWorldPublicationKeepsNestedCropsInPainterOrder()
    {
        using UITestShaderScope shaders = new();
        using IDisposable snippets = shaders.RegisterCanonicalCropSnippets();
        using IDisposable materialTarget =
            RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
                EngineMaterialConstructionTarget.DesktopGlsl);
        UICanvasTransform canvasTransform = new()
        {
            Width = 100,
            Height = 100,
            DrawSpace = ECanvasDrawSpace.World,
        };
        SceneNode root = new("World multi-record publication", canvasTransform)
        {
            IsActiveSelf = false,
        };
        root.SetWorldContext(new TestWorldContext());
        UIPublication? publication = null;
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            canvas.PreferOffscreenRenderingForNonScreenSpaces = false;

            SceneNode outerNode = new(root) { Name = "Outer clip" };
            UIBoundableTransform outer = outerNode.SetTransform<UIBoundableTransform>();
            outerNode.AddComponent<UIPublicationMutationProbe>()!.ClipToBounds = true;
            SceneNode innerNode = new(outerNode) { Name = "Inner clip" };
            UIBoundableTransform inner = innerNode.SetTransform<UIBoundableTransform>();
            innerNode.AddComponent<UIPublicationMutationProbe>()!.ClipToBounds = true;

            UIMaterialComponent first = Quad(innerNode, "First attached inner", 2);
            UIMaterialComponent second = Quad(innerNode, "Second attached inner", 1);
            UIMaterialComponent last = Quad(outerNode, "Last outer", 3);
            root.IsActiveSelf = true;
            Apply(canvasTransform, new Vector2(100), Vector2.Zero);
            Apply(outer, new Vector2(80), new Vector2(10));
            Apply(inner, new Vector2(40), new Vector2(20));
            foreach (UIMaterialComponent component in new[] { first, second, last })
                Apply(component.BoundableTransform, new Vector2(24), Vector2.Zero);
            canvasTransform.RecalculateMatrixHierarchyImmediate();
            outer.RefreshAxisAlignedRegion();
            inner.RefreshAxisAlignedRegion();

            // Painter order intentionally differs from child attachment order.
            UIMaterialComponent[] painterOrder = [second, first, last];
            UIRenderRecord[] records = painterOrder.Select(component =>
                component.CapturePublicationRenderRecord(new UILayoutNodeOutput
                {
                    ActualSize = new Vector2(24),
                    Visible = true,
                })).ToArray();
            foreach (UIRenderRecord record in records)
            {
                record.Visible.ShouldBeTrue();
                record.CaptureFailure.ShouldBeNull();
                record.FrozenWorldCommand.ShouldNotBeNull();
                record.Payload.ShouldNotBeNull();
            }
            BoundingRectangle? resolvedInner = UIClipRegion.ResolveCrop(
                first.BoundableTransform, false);
            BoundingRectangle? resolvedOuter = UIClipRegion.ResolveCrop(
                last.BoundableTransform, false);
            resolvedInner.ShouldNotBeNull();
            resolvedOuter.ShouldNotBeNull();
            BoundingRectangle innerCrop = resolvedInner.Value;
            BoundingRectangle outerCrop = resolvedOuter.Value;
            innerCrop.ShouldNotBe(outerCrop);
            records[0].Crop.ShouldBe(innerCrop);
            records[1].Crop.ShouldBe(innerCrop);
            records[2].Crop.ShouldBe(outerCrop);

            publication = new UIPublication(1, 1, 1, 1, 1,
                ReadOnlyMemory<UILayoutNodeOutput>.Empty,
                ReadOnlyMemory<UIElementIdentity>.Empty,
                [], records, canvasTransform.GetActualBounds(),
                Matrix4x4.Identity, Matrix4x4.Identity,
                authoringDrawSpace: ECanvasDrawSpace.World,
                authoringOffscreen: false);
            publication.BuildWorldCommands(canvas);
            var commands = publication.WorldCommands.ToArray();
            commands.Length.ShouldBe(3);
            for (int index = 0; index < commands.Length; index++)
            {
                UIRenderRecord record = records[index];
                commands[index].Publication.ShouldBeSameAs(publication);
                record.SourceWorldCommand.ShouldBeSameAs(
                    painterOrder[index].RenderCommand3D);
                commands[index].SourceWorldCommand.ShouldBeSameAs(
                    painterOrder[index].RenderCommand3D);
                commands[index].Mesh.ShouldBeSameAs(record.FrozenWorldCommand!.Mesh);
                commands[index].WorldMatrix.ShouldBe(record.FrozenWorldCommand.WorldMatrix);
                commands[index].WorldCrop.ShouldBe(record.Crop);
                commands[index].WorldCropMinMax.ShouldBe(
                    record.Payload!.WorldCrop!.Value.MinMax);
                commands[index].WorldCropModelToCanvas.ShouldBe(
                    record.Payload.WorldCrop.Value.ModelToCanvas);
                commands[index].WorldCropFragmentShader.ShouldBeSameAs(
                    record.Payload.WorldCropFragmentShader);
                commands[index].WorldCropVertexShader.ShouldBeSameAs(
                    record.Payload.WorldCropVertexShader);
            }

            Apply(inner, new Vector2(40), new Vector2(25));
            canvasTransform.RecalculateMatrixHierarchyImmediate();
            inner.RefreshAxisAlignedRegion();
            UIClipRegion.ResolveCrop(first.BoundableTransform, false)
                .ShouldNotBe(innerCrop);
            publication.WorldCommands[0].WorldCrop.ShouldBe(innerCrop);
            publication.WorldCommands[1].WorldCrop.ShouldBe(innerCrop);
            publication.WorldCommands[2].WorldCrop.ShouldBe(outerCrop);
        }
        finally
        {
            publication?.Release();
            root.Destroy(true);
        }
    }

    private static UIMaterialComponent Quad(SceneNode parent, string name, int zIndex)
    {
        SceneNode node = new(parent) { Name = name };
        node.SetTransform<UIBoundableTransform>();
        UIMaterialComponent component = node.AddComponent<UIMaterialComponent>()!;
        component.RenderCommand2D.ZIndex = zIndex;
        component.RenderInfo3D.IsVisible = true;
        component.RenderInfo3D.CastsShadows = false;
        component.RenderPass = (int)EDefaultRenderPass.TransparentForward;
        return component;
    }

    private static void Apply(UIBoundableTransform transform, Vector2 size,
        Vector2 translation)
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
