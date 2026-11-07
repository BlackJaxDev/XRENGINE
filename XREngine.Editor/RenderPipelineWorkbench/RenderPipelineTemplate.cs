namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>A starting pipeline that the new pipeline wizard offers. The pipeline is an <c>.xrs</c> file.</summary>
/// <param name="DisplayName">The name that the wizard shows.</param>
/// <param name="FileName">The file name of the template in the template folder.</param>
/// <param name="Description">One sentence that tells what the template renders.</param>
public sealed record RenderPipelineTemplate(string DisplayName, string FileName, string Description);
