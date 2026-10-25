using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeAssetMenuRequestTests
{
    [TestCase("unchanged", true, true)]
    [TestCase("row", false, true)]
    [TestCase("row-node", false, true)]
    [TestCase("row-transform", false, true)]
    [TestCase("owner", false, false)]
    [TestCase("owner-node", false, false)]
    [TestCase("owner-transform", false, false)]
    [TestCase("list", false, true)]
    [TestCase("canvas", false, false)]
    [TestCase("input", false, false)]
    [TestCase("provider", false, true)]
    [TestCase("recycle", false, false)]
    [TestCase("refresh", false, true)]
    [TestCase("moved-list", false, true)]
    [TestCase("inactive", false, false)]
    [TestCase("field", true, false)]
    [TestCase("field-node", true, false)]
    [TestCase("field-transform", true, false)]
    [TestCase("moved-field", true, false)]
    [TestCase("selection", true, false)]
    [TestCase("path-replaced", true, false)]
    [TestCase("path-removed", true, false)]
    public void MenuAndRenameKeepTheirRespectiveRowAndDraftOwners(string change, bool expected, bool canRename)
    {
        using UITestShaderScope shaders = new();
        UICanvasTransform canvas = new();
        canvas.SetSize(new Vector2(240, 120));
        SceneNode root = new("Asset menu canvas", canvas) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        UITestCanvasInputProbe input = root.AddComponent<UITestCanvasInputProbe>()!;
        SceneNode panel = new(root);
        panel.SetTransform<UIBoundableTransform>();
        UIComponent owner = panel.AddComponent<UIComponent>()!;
        SceneNode fieldNode = new(panel);
        fieldNode.SetTransform<UIBoundableTransform>();
        UITextInputComponent field = fieldNode.AddComponent<UITextInputComponent>()!;
        SceneNode listNode = new(panel);
        UIListTransform list = listNode.SetTransform<UIListTransform>();
        UIVirtualTableRefreshSource source = new();
        UIVirtualTableProvider<string> provider = new(source,
            new UIVirtualTableColumn<string>("name", "Name", 120,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                (_, _, _) => { }, _ => { }));
        list.ItemSize = 20;
        list.VirtualOverscan = 0;
        root.IsActiveSelf = true;
        list.VirtualProvider = provider;
        try
        {
            list.SynchronizeVirtualRows(20);
            list.TryGetRealizedVirtualRow(11, out var bounds, out var lease).ShouldBeTrue();
            UIVirtualTableRowComponent row = bounds!.SceneNode!.GetComponent<UIVirtualTableRowComponent>()!;
            NativeAssetMenuRequest request = new(owner, row, list, provider, lease, canvas, input,
                new Vector2(8, 70), "selected.asset");
            NativeAssetRenameDraft draft = new(request, field);
            string selectedPath = "selected.asset";
            Dictionary<ulong, string> paths = new() { [11] = selectedPath };
            request.IsCurrent().ShouldBeTrue();
            draft.CanSubmit(field, selectedPath, paths).ShouldBeTrue();
            if (change == "unchanged")
            {
                for (int index = 0; index < 20; index++) request.IsCurrent();
                long before = GC.GetAllocatedBytesForCurrentThread();
                bool current = true;
                for (int index = 0; index < 100; index++) current &= request.IsCurrent();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                current.ShouldBeTrue();
                allocated.ShouldBe(0);
            }

            switch (change)
            {
                case "row": row.Generate(); break;
                case "row-node": row.SceneNode.Generate(); break;
                case "row-transform": row.Transform.Generate(); break;
                case "owner": owner.Generate(); break;
                case "owner-node": panel.Generate(); break;
                case "owner-transform": panel.Transform.Generate(); break;
                case "list": list.Generate(); break;
                case "canvas": canvas.Generate(); break;
                case "input": input.Generate(); break;
                case "provider": list.VirtualProvider = null; list.VirtualProvider = provider; break;
                case "recycle": source.Replace(22, "Replacement"); list.SynchronizeVirtualRows(20); paths.Remove(11); break;
                case "refresh": source.AdvanceRevision(); break;
                case "moved-list": list.Parent = root.Transform; break;
                case "inactive": panel.IsActiveSelf = false; break;
                case "field": field.Generate(); break;
                case "field-node": fieldNode.Generate(); break;
                case "field-transform": field.Transform.Generate(); break;
                case "moved-field": field.Transform.Parent = root.Transform; break;
                case "selection": selectedPath = "other.asset"; break;
                case "path-replaced": paths[11] = "other.asset"; break;
                case "path-removed": paths.Remove(11); break;
            }

            request.IsCurrent().ShouldBe(expected);
            draft.CanSubmit(field, selectedPath, paths).ShouldBe(canRename);
            request.Path.ShouldBe("selected.asset");
            request.Lease.ShouldBe(lease);
        }
        finally { root.Destroy(true); }
    }
}
