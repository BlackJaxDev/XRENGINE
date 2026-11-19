namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkRenderProgram
{
    private VulkanShaderArtifact? _editorHighlightCertifiedArtifact;
    private ulong _editorHighlightCertifiedLinkGeneration;
    private uint _editorHighlightCertifiedLocation;

    /// <summary>
    /// Verifies the linked fragment outputs against the auxiliary mask attachment.
    /// </summary>
    internal bool TryValidateEditorHighlightFragmentOutput(uint maskLocation, out string failureReason)
    {
        VulkanShaderArtifact? fragment = null;
        foreach (VkShader shader in _shaderCache.Values)
        {
            if (shader.Data.Type != EShaderType.Fragment)
                continue;
            if (fragment is not null)
            {
                failureReason = "The linked program has more than one fragment shader.";
                return false;
            }
            fragment = shader.LastArtifact;
        }

        if (fragment is null)
        {
            failureReason = "The linked fragment artifact is unavailable.";
            return false;
        }
        if (ReferenceEquals(fragment, _editorHighlightCertifiedArtifact) &&
            _editorHighlightCertifiedLinkGeneration == LinkGeneration &&
            _editorHighlightCertifiedLocation == maskLocation)
        {
            failureReason = string.Empty;
            return true;
        }

        if (!VulkanShaderReflection.ValidateEditorHighlightOutputs(
                fragment.SpirV, maskLocation, 2, out failureReason))
            return false;

        _editorHighlightCertifiedArtifact = fragment;
        _editorHighlightCertifiedLinkGeneration = LinkGeneration;
        _editorHighlightCertifiedLocation = maskLocation;
        return true;
    }
}
