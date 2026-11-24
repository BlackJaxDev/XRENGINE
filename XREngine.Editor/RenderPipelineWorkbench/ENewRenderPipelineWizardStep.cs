namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>The pages of the new pipeline wizard, in order.</summary>
public enum ENewRenderPipelineWizardStep
{
    /// <summary>Select the source of the pipeline.</summary>
    Source,

    /// <summary>Select the stages and the base arguments.</summary>
    Features,

    /// <summary>Select the output file.</summary>
    Output,

    /// <summary>Read the script and its problems, then create the file.</summary>
    Review,
}
