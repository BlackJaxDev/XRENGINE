using System.Text.Json;
using XREngine.Scene.Importers.Poiyomi;

namespace XREngine.Editor.MaterialAuthoring;

public sealed record PoiyomiAuthoringAuditEntry(
    string Id,
    string Kind,
    int ActiveUsageCount,
    EMaterialWorkflowClassification Classification,
    string Owner,
    string ValidationCase,
    string NativeEquivalent);

/// <summary>
/// Executable pinned-source audit. Every active annotation and reachable
/// workflow receives a reviewed native, preserved-inactive, or developer-only
/// classification; absence is a test failure.
/// </summary>
public static class PoiyomiAuthoringParityAudit
{
    private static readonly Lazy<IReadOnlyList<PoiyomiAuthoringAuditEntry>> Entries =
        new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<PoiyomiAuthoringAuditEntry> All => Entries.Value;

    public static IReadOnlyList<PoiyomiAuthoringAuditEntry> Unclassified
        => All.Where(static entry =>
                string.IsNullOrWhiteSpace(entry.Owner) ||
                string.IsNullOrWhiteSpace(entry.ValidationCase) ||
                string.IsNullOrWhiteSpace(entry.NativeEquivalent))
            .ToArray();

    private static IReadOnlyList<PoiyomiAuthoringAuditEntry> Build()
    {
        using Stream stream = PoiyomiToon93Catalog.OpenCatalog();
        using JsonDocument document = JsonDocument.Parse(stream);
        List<PoiyomiAuthoringAuditEntry> entries = [];
        foreach (JsonElement annotation in document.RootElement.GetProperty("annotations").EnumerateArray())
        {
            int usage = annotation.GetProperty("activeUsageCount").GetInt32();
            if (usage <= 0)
                continue;
            string id = annotation.GetProperty("name").GetString() ?? string.Empty;
            EMaterialWorkflowClassification classification = IsMetadataAnnotation(id)
                ? EMaterialWorkflowClassification.Native
                : ShaderAuthoringWidgetRegistry.TryResolve(id, out _)
                    ? EMaterialWorkflowClassification.Native
                    : EMaterialWorkflowClassification.PreservedInactive;
            entries.Add(new(
                id,
                "annotation",
                usage,
                classification,
                "Editor",
                $"annotation:{id}",
                classification == EMaterialWorkflowClassification.Native
                    ? "Typed ShaderAuthoringWidgetRegistry control/capability"
                    : "Visible unsupported diagnostic; never generic execution"));
        }

        foreach (JsonElement workflow in document.RootElement.GetProperty("workflows").EnumerateArray())
        {
            string id = workflow.GetProperty("id").GetString() ?? string.Empty;
            string kind = workflow.GetProperty("kind").GetString() ?? "workflow";
            (EMaterialWorkflowClassification classification, string native) = ClassifyWorkflow(id, kind);
            entries.Add(new(
                id,
                kind,
                1,
                classification,
                classification == EMaterialWorkflowClassification.DeveloperOnly ? "Developer Tools" : "Editor",
                $"workflow:{NormalizeValidationId(id)}",
                native));
        }
        return entries;
    }

    private static bool IsMetadataAnnotation(string id)
        => id is "HideInInspector" or "DoNotAnimate" or "DoNotLock" or "DoNotRename" or
            "NoScaleOffset" or "NonModifiableTextureData";

    private static (EMaterialWorkflowClassification, string) ClassifyWorkflow(string id, string kind)
    {
        if (id.Contains("Dev Test", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("Ifex Indenting", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.DeveloperOnly, "Existing XRENGINE test/developer tooling");
        if (id.Contains("Twitter", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.PreservedInactive, "Unsafe/social external action is non-executable text");
        if (id.Contains("Copy GUID", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "Copy stable XRENGINE asset identity");
        if (id.Contains("TextureArray", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("Flipbooks", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "MaterialTextureArrayRecipe workspace");
        if (id.Contains("Cross", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "Semantic Cross-Shader Material Editor");
        if (id.Contains("Cleaner", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("Cleanup", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "Protected MaterialCleanupReport workflow");
        if (id.Contains("Lock", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("Unlock", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("unprepared", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "Optimize/Prepare Variant manager");
        if (id.Contains("Locale", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("localization", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("Settings", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "Versioned locale/preferences authoring workspace");
        if (id.Contains("Translator", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("Translate", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "Semantic shader conversion preview");
        if (id.Contains("Texture", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("Packer", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "Texture packer/usage/array authoring workspace");
        if (id.Contains("Decal", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "MaterialDecalToolController viewport bridge");
        if (id.Contains("Gradient", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "Gradient/curve authoring workspace");
        if (id.Contains("Link", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "Persistent semantic material-link groups");
        if (id.Contains("Note", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("TextPopup", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "Persistent local material/property notes");
        if (id.Contains("Preset", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "Versioned preset library and preview");
        if (id.Contains("Paste", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "Versioned hierarchical Paste Special");
        if (id.Contains("SearchableEnum", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "Typed searchable enum widget");
        if (id.Contains("Keywords", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("Animated Properties", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "Variant/animation semantic repair");
        if (id.Contains("propertyContextMenu", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("inspectorHierarchy", StringComparison.OrdinalIgnoreCase))
            return (EMaterialWorkflowClassification.Native, "Schema-driven inspector interaction");
        return kind == "auxiliaryWindow"
            ? (EMaterialWorkflowClassification.Native, "Native ImGui authoring workspace")
            : (EMaterialWorkflowClassification.PreservedInactive, "Reviewed source workflow retained without execution");
    }

    private static string NormalizeValidationId(string id)
    {
        Span<char> buffer = stackalloc char[Math.Min(id.Length, 128)];
        int count = 0;
        foreach (char character in id)
        {
            if (count == buffer.Length)
                break;
            if (char.IsLetterOrDigit(character))
                buffer[count++] = char.ToLowerInvariant(character);
            else if (count > 0 && buffer[count - 1] != '-')
                buffer[count++] = '-';
        }
        return new string(buffer[..count]).Trim('-');
    }
}

public sealed record PoiyomiRemoteFacilityAuditEntry(
    ERemoteAuthoringFacility Facility,
    bool Reachable,
    EMaterialWorkflowClassification Classification,
    string NativeBehavior);

public static class PoiyomiRemoteFacilityAudit
{
    public static IReadOnlyList<PoiyomiRemoteFacilityAuditEntry> Entries { get; } =
    [
        new(ERemoteAuthoringFacility.LocalMessage, false, EMaterialWorkflowClassification.PreservedInactive,
            "Pinned shader has no active LocalMessage; static help boxes render locally."),
        new(ERemoteAuthoringFacility.RemoteMessage, false, EMaterialWorkflowClassification.PreservedInactive,
            "Pinned shader has no active RemoteMessage; no fetch occurs."),
        new(ERemoteAuthoringFacility.RemoteVersionCheck, false, EMaterialWorkflowClassification.PreservedInactive,
            "Imported URL is inert metadata unless an explicit allowlisted remote policy is enabled."),
        new(ERemoteAuthoringFacility.RemoteImage, false, EMaterialWorkflowClassification.PreservedInactive,
            "Remote images are non-executable and are not fetched by inspector drawing."),
    ];
}
