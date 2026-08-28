namespace XREngine.Core.Files.Caching;

/// <summary>Stable cache capabilities that core loading can request without referencing feature-owned asset types.</summary>
public static class ThirdPartyCacheAuthorityRoles
{
    /// <summary>Resolves the current texture streaming cache authority.</summary>
    public const string TextureStreaming = "TextureStreaming";
}
