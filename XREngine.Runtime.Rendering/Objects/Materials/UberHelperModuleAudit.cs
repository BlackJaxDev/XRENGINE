namespace XREngine.Rendering;

/// <summary>
/// Authoritative Phase 4 audit of every helper under the canonical Uber shader directory.
/// </summary>
public static class UberHelperModuleAudit
{
    public static IReadOnlyList<UberHelperModuleAuditEntry> Entries { get; } =
    [
        new("common.glsl", EUberHelperModuleStatus.Active, true, "Shared math and render snippets."),
        new("uniforms.glsl", EUberHelperModuleStatus.Active, true, "Canonical manifest and uniform declarations."),
        new("parallax.glsl", EUberHelperModuleStatus.Active, true, "Parallax feature implementation."),
        new("dissolve.glsl", EUberHelperModuleStatus.Active, true, "Dissolve and opacity coverage."),
        new("glitter.glsl", EUberHelperModuleStatus.Active, true, "Glitter feature implementation."),
        new("flipbook.glsl", EUberHelperModuleStatus.Active, true, "Flipbook feature implementation."),
        new("outline.vert", EUberHelperModuleStatus.Reusable, false, "Legacy standalone reference; the active pass variants the canonical vertex shaders."),
        new("outline.frag", EUberHelperModuleStatus.Reusable, false, "Legacy standalone reference; the active pass variants the canonical fragment shader."),
        new("backface.glsl", EUberHelperModuleStatus.Reusable, false, "Reusable reference; canonical fragment currently owns the active implementation."),
        new("decals.glsl", EUberHelperModuleStatus.Dormant, false, "Four-slot reusable implementation reserved for the decal parity phase."),
        new("details.glsl", EUberHelperModuleStatus.Reusable, false, "Reusable reference; canonical fragment currently owns the active implementation."),
        new("emission.glsl", EUberHelperModuleStatus.Reusable, false, "Reusable multi-slot implementation; canonical fragment owns the active single-slot path."),
        new("matcap.glsl", EUberHelperModuleStatus.Reusable, false, "Reusable multi-slot implementation; canonical fragment owns the active single-slot path."),
        new("outlines.glsl", EUberHelperModuleStatus.Obsolete, false, "Superseded by the canonical XRENGINE_OUTLINE_PASS shader branch."),
        new("pbr.glsl", EUberHelperModuleStatus.Reusable, false, "Reusable reference; canonical fragment deliberately keeps its forward-lighting implementation inline."),
        new("specular.glsl", EUberHelperModuleStatus.Reusable, false, "Reusable reference; canonical fragment owns the active implementation."),
        new("subsurface.glsl", EUberHelperModuleStatus.Reusable, false, "Reusable reference; canonical fragment owns the active implementation."),
    ];
}
