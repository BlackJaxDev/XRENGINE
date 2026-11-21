namespace XREngine.Rendering;

/// <summary>
/// Pipeline resources of the mesh surface decal buffer. The decal buffer pass writes them after
/// the final visibility depth, and native opaque shading reads them before lighting.
/// </summary>
public static class AdvancedDecalBufferResourceNames
{
    public const string ScopePrefix = "AdvancedDecalBuffer";

    /// <summary>RGBA8: rgb = albedo x coverage (premultiplied), a = accumulated albedo coverage.</summary>
    public const string Albedo = ScopePrefix + ".Albedo";

    /// <summary>RGBA8: r = roughness x coverage, g = metallic x coverage, b = reserved, a = accumulated surface coverage.</summary>
    public const string Surface = ScopePrefix + ".Surface";

    /// <summary>Framebuffer with both decal targets and the read-only visibility depth.</summary>
    public const string FrameBuffer = ScopePrefix + ".FBO";
}
