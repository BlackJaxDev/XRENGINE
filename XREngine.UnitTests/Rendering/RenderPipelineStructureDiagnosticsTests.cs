using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderPipelineStructureDiagnosticsTests
{
    [Test]
    public void EveryConcreteCommandType_HasRenderCommandInfo()
    {
        Type baseType = typeof(ViewportRenderCommand);
        List<string> missing = [];
        foreach (Type type in baseType.Assembly.GetTypes())
        {
            if (type.IsAbstract || type.ContainsGenericParameters || !baseType.IsAssignableFrom(type))
                continue;

            if (!RenderCommandInfo.Get(type).HasAttribute)
                missing.Add(type.FullName ?? type.Name);
        }

        missing.ShouldBeEmpty();
    }

    [Test]
    public void FlowCommands_DoNotClaimGpuWork()
    {
        RenderCommandInfo.Get(typeof(VPRC_IfElse)).Category.ShouldBe(ERenderCommandCategory.Flow);
        RenderCommandInfo.Get(typeof(VPRC_IfElse)).DoesGpuWork.ShouldBeFalse();
        RenderCommandInfo.Get(typeof(VPRC_RenderQuadToFBO)).DoesGpuWork.ShouldBeTrue();
    }

    [Test]
    public void Catalog_ListsRegisteredCommands_SortedByCategory()
    {
        ViewportRenderCommandContainer.RegisterCommandFactory<VPRC_IfElse>();
        ViewportRenderCommandContainer.RegisterCommandFactory<VPRC_RenderQuadToFBO>();

        IReadOnlyList<RenderCommandCatalogEntry> entries = RenderCommandCatalog.Entries;
        RenderCommandCatalog.TryGet(typeof(VPRC_IfElse), out RenderCommandCatalogEntry? ifElse).ShouldBeTrue();
        ifElse!.ScriptMethodName.ShouldBe("ifElse");
        ifElse.Info.Category.ShouldBe(ERenderCommandCategory.Flow);

        for (int i = 1; i < entries.Count; i++)
            ((int)entries[i - 1].Info.Category).ShouldBeLessThanOrEqualTo((int)entries[i].Info.Category);
    }

    [Test]
    public void StructureSnapshot_WalksBranchContainers_InPreOrder()
    {
        ViewportRenderCommandContainer root = new();
        VPRC_IfElse branch = root.Add<VPRC_IfElse>();
        branch.Label = "Test branch";
        branch.TrueCommands = new ViewportRenderCommandContainer();
        branch.TrueCommands.Add<VPRC_SetVariable>();
        branch.FalseCommands = new ViewportRenderCommandContainer();
        branch.FalseCommands.Add<VPRC_Annotation>();
        root.Add<VPRC_Annotation>();

        RenderPipelineStructureSnapshot snapshot = RenderPipelineStructureSnapshot.Build(1, root, null);

        snapshot.Commands.Count.ShouldBe(4);
        snapshot.Commands[0].Command.ShouldBeSameAs(branch);
        snapshot.Commands[0].SubtreeEnd.ShouldBe(3);
        snapshot.Commands[1].ContainerName.ShouldBe("True");
        snapshot.Commands[1].ParentIndex.ShouldBe(0);
        snapshot.Commands[2].ContainerName.ShouldBe("False");
        snapshot.Commands[3].ParentIndex.ShouldBe(-1);
        snapshot.GpuCommandsWithoutPasses.ShouldBeEmpty();
    }

    [Test]
    public void BranchTrace_KeepsTheLastContainerForEachCommand()
    {
        RenderPipelineBranchTrace trace = new();
        VPRC_IfElse branch = new();
        ViewportRenderCommandContainer taken = new();
        ViewportRenderCommandContainer other = new();

        trace.HasRecords.ShouldBeFalse();
        trace.Record(branch, other);
        trace.Record(branch, taken);

        trace.HasRecords.ShouldBeTrue();
        trace.TryGet(branch, out RenderPipelineBranchRecord record).ShouldBeTrue();
        record.Container.ShouldBeSameAs(taken);

        int version = trace.Version;
        trace.Clear();
        trace.HasRecords.ShouldBeFalse();
        trace.Version.ShouldNotBe(version);
        trace.TryGet(branch, out _).ShouldBeFalse();
    }
}
