namespace XREngine;

/// <summary>Adds auxiliary work to the ordinary output DAG.</summary>
public static class AuxiliaryOutputGraphBuilder
{
    public static int AddDesktopMirror(
        RenderOutputDag graph,
        ulong nodeKey,
        ulong outputKey,
        ulong viewKey,
        int renderedEyeNode,
        bool independentCamera,
        int sharedPublicationNode)
    {
        int node = graph.AddNode(new(
            nodeKey,
            outputKey,
            independentCamera ? ERenderOutputDagNodeKind.SceneView : ERenderOutputDagNodeKind.ComposeMirror,
            independentCamera ? ERenderOutputDataClass.ViewDependent : ERenderOutputDataClass.ViewIndependent,
            independentCamera ? viewKey : 0UL,
            outputKey,
            1u,
            Cacheable: false,
            Resumable: false,
            independentCamera ? "Independent desktop scene" : "Composed desktop VR mirror"));
        if (node >= 0)
        {
            int dependency = independentCamera ? sharedPublicationNode : renderedEyeNode;
            if (dependency >= 0)
                graph.AddDependency(dependency, node);
        }
        return node;
    }

    public static int AddCapture(
        RenderOutputDag graph,
        ulong nodeKey,
        ulong outputKey,
        ulong viewKey,
        uint maximumContentAgeFrames,
        bool cacheLastResult,
        int sharedPublicationNode)
    {
        int node = graph.AddNode(new(
            nodeKey,
            outputKey,
            ERenderOutputDagNodeKind.Capture,
            ERenderOutputDataClass.ViewDependent,
            viewKey,
            outputKey,
            maximumContentAgeFrames,
            cacheLastResult,
            Resumable: false,
            "Scene capture"));
        if (node >= 0 && sharedPublicationNode >= 0)
            graph.AddDependency(sharedPublicationNode, node);
        return node;
    }

    public static int AddViewDependentOutput(
        RenderOutputDag graph,
        in AuxiliaryOutputPolicy policy,
        ulong viewKey,
        ulong nodeKey,
        int sharedPublicationNode)
    {
        policy.Validate();
        int node = graph.AddNode(new(
            nodeKey,
            policy.StableOutputKey,
            ERenderOutputDagNodeKind.SceneView,
            ERenderOutputDataClass.ViewDependent,
            viewKey,
            policy.StableOutputKey,
            policy.MaximumContentAgeFrames,
            policy.CacheLastResult,
            Resumable: false,
            "View-dependent auxiliary output"));
        if (node >= 0 && sharedPublicationNode >= 0)
            graph.AddDependency(sharedPublicationNode, node);
        return node;
    }

    public static int AddProbePipeline(
        RenderOutputDag graph,
        ulong outputKey,
        ulong nodeKeyBase,
        int faceCount,
        int prefilterMipCount,
        int sharedPublicationNode)
    {
        if (faceCount < 1 || prefilterMipCount < 1)
            throw new ArgumentOutOfRangeException(nameof(faceCount));

        for (int face = 0; face < faceCount; face++)
            _ = AddPersistentNode(graph, nodeKeyBase + (ulong)face, outputKey,
                ERenderOutputDagNodeKind.ProbeFace, ERenderOutputDataClass.ViewDependent,
                outputKey + (ulong)face + 1UL, sharedPublicationNode, "Probe face");

        int mip = AddPersistentNode(graph, nodeKeyBase + 0x100UL, outputKey,
            ERenderOutputDagNodeKind.GenerateMip, ERenderOutputDataClass.ViewIndependent,
            0UL, dependency: -1, "Probe mip generation");
        for (int face = 0; face < faceCount; face++)
            if (graph.TryGetNodeIndex(nodeKeyBase + (ulong)face, out int faceNode))
                graph.AddDependency(faceNode, mip);
        int octa = AddPersistentNode(graph, nodeKeyBase + 0x200UL, outputKey,
            ERenderOutputDagNodeKind.OctahedralConversion, ERenderOutputDataClass.ViewIndependent,
            0UL, mip, "Probe octahedral conversion");
        _ = AddPersistentNode(graph, nodeKeyBase + 0x300UL, outputKey,
            ERenderOutputDagNodeKind.Irradiance, ERenderOutputDataClass.ViewIndependent,
            0UL, octa, "Probe irradiance");

        int terminal = graph.AddNode(new(
            nodeKeyBase + 0x800UL,
            outputKey,
            ERenderOutputDagNodeKind.Publish,
            ERenderOutputDataClass.ViewIndependent,
            0UL,
            outputKey,
            uint.MaxValue,
            Cacheable: true,
            Resumable: true,
            "Completed probe result"));
        if (terminal >= 0 && graph.TryGetNodeIndex(nodeKeyBase + 0x300UL, out int irradiance))
            graph.AddDependency(irradiance, terminal);
        for (int index = 0; index < prefilterMipCount; index++)
        {
            int prefilter = AddPersistentNode(graph, nodeKeyBase + 0x400UL + (ulong)index, outputKey,
                ERenderOutputDagNodeKind.PrefilterMip, ERenderOutputDataClass.ViewIndependent,
                0UL, octa, "Probe prefilter mip");
            if (terminal >= 0 && prefilter >= 0)
                graph.AddDependency(prefilter, terminal);
        }
        return terminal;
    }

    public static int AddPostProcess(
        RenderOutputDag graph,
        ulong nodeKey,
        ulong outputKey,
        int sourceNode,
        bool enabled)
    {
        if (!enabled)
            return sourceNode;
        int node = graph.AddNode(new(
            nodeKey,
            outputKey,
            ERenderOutputDagNodeKind.PostProcess,
            ERenderOutputDataClass.ViewDependent,
            outputKey,
            0UL,
            0u,
            Cacheable: false,
            Resumable: false,
            "Output-local post process"));
        if (sourceNode >= 0 && node >= 0)
            graph.AddDependency(sourceNode, node);
        return node;
    }

    private static int AddPersistentNode(
        RenderOutputDag graph,
        ulong nodeKey,
        ulong outputKey,
        ERenderOutputDagNodeKind kind,
        ERenderOutputDataClass dataClass,
        ulong viewKey,
        int dependency,
        string name)
    {
        int node = graph.AddNode(new(
            nodeKey,
            outputKey,
            kind,
            dataClass,
            viewKey,
            outputKey,
            uint.MaxValue,
            Cacheable: true,
            Resumable: true,
            name));
        if (dependency >= 0 && node >= 0)
            graph.AddDependency(dependency, node);
        return node;
    }
}