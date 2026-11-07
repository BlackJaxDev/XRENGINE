using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.RenderGraph.Diagnostics;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Diagnostics;

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

    [Test]
    public void ResourceSnapshot_MapsAttachmentsToTextures_AndFindsProblems()
    {
        RenderPipelineResourceProfile profile = RenderPipelineResourceProfile.Empty with { InternalWidth = 100u, InternalHeight = 50u };
        RenderPipelineResourceLayout layout = new RenderPipelineResourceLayoutBuilder(profile)
            .Texture("HDRColor").Size(RenderResourceSizePolicy.Internal()).SizedFormat(ESizedInternalFormat.Rgba16f).Add()
            .Texture("Unused").Size(RenderResourceSizePolicy.Absolute(4u, 4u)).SizedFormat(ESizedInternalFormat.Rgba8).Add()
            .FrameBuffer("HDRFBO").Color(0, "HDRColor").Add()
            .Build(profile);

        RenderPassMetadataCollection passes = new();
        passes.ForPass(0, "Scene").UseColorAttachment(RenderGraphResourceNames.MakeFboColor("HDRFBO"), ERenderGraphAccess.Write);
        passes.ForPass(1, "Tonemap")
            .SampleTexture(RenderGraphResourceNames.MakeTexture("HDRColor"))
            .SampleTexture(RenderGraphResourceNames.MakeTexture("Missing"));

        RenderPipelineResourceSnapshot snapshot = RenderPipelineResourceSnapshot.Build(layout, passes.Build(), null);

        snapshot.TryGet("HDRColor", out RenderPipelineResourceEntry? color).ShouldBeTrue();
        color!.Producers.ShouldBe([0]);
        color.Consumers.ShouldBe([1]);
        color.EstimatedBytes.ShouldBe(8ul * 100ul * 50ul);
        color.FirstUseOrder.ShouldBe(0);
        color.LastUseOrder.ShouldBe(1);

        snapshot.TryGet("Unused", out RenderPipelineResourceEntry? unused).ShouldBeTrue();
        unused!.IsDeclaredButUnused.ShouldBeTrue();
        snapshot.TryGet("Missing", out RenderPipelineResourceEntry? missing).ShouldBeTrue();
        missing!.IsUsedButUndeclared.ShouldBeTrue();
        snapshot.DeclaredUnusedCount.ShouldBe(1);
        snapshot.UsedUndeclaredCount.ShouldBe(1);
        snapshot.TotalBytes.ShouldBe((8ul * 100ul * 50ul) + (4ul * 4ul * 4ul));
    }

    [Test]
    public void StageConsumers_DeclareTheirSchemaStages()
    {
        RenderCommandInfo.Get(typeof(VPRC_BloomPass)).ConsumesStages.ShouldContain(XREngine.Rendering.PostProcessing.CommonPostProcessStages.StageKeys.Bloom);
        RenderCommandInfo.Get(typeof(VPRC_Tonemap)).ConsumesStages.ShouldContain(XREngine.Rendering.PostProcessing.CommonPostProcessStages.StageKeys.Tonemapping);
        RenderCommandInfo.Get(typeof(VPRC_SSAOPass)).ConsumesStages.ShouldContain(XREngine.Rendering.PostProcessing.CommonPostProcessStages.StageKeys.AmbientOcclusion);
    }
}
