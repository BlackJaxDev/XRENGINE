using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace XREngine.Editor.Publishing;

/// <summary>Reports metadata references which cannot be linked into an authored browser game.</summary>
internal static class BrowserGameAssemblyAudit
{
    // These unavailable assemblies and APIs have no reliable browser platform attributes.
    private static readonly string[] DesktopAssemblies =
    [
        "XREngine.Runtime.Bootstrap", "XREngine.Runtime.ModelAssetPipeline", "XREngine.Runtime.Physics.PhysX",
        "XREngine.Runtime.Platform.Desktop", "XREngine.Runtime.VR", "XREngine.Runtime.Rendering.OpenGL",
        "XREngine.Runtime.Rendering.Vulkan", "XREngine.Runtime.Rendering.ImGui", "XREngine.Runtime.XR.OpenVR",
        "XREngine.Runtime.XR.OpenXR", "XREngine.Runtime.Physics.Jitter", "XREngine.Runtime.Physics.Authoring",
        "XREngine.Runtime.Diagnostics.Desktop", "XREngine.Runtime.IO.DirectStorage", "XREngine.Runtime.Imaging.Magick",
        "XREngine.Runtime.Media.FFmpeg", "XREngine.Runtime.MeshProcessing.Meshoptimizer", "XREngine.Runtime.Net.Sockets",
        "XREngine.Runtime.Net.Osc", "XREngine.Runtime.Text.FreeType", "XREngine.Runtime.UI.Skia",
        "XREngine.Runtime.UI.Ultralight", "XREngine.Runtime.UI.Rive", "XREngine.Runtime.Automation",
        "XREngine.Audio.OpenAL", "XREngine.Audio.NAudio", "XREngine.Audio.SteamAudio",
        "XREngine.Audio.OVRLipSync", "XREngine.Audio.Audio2Face", "XREngine.Input.Silk", "XREngine.Input.XInput",
        "XREngine.Editor", "XREngine.Fbx", "XREngine.Gltf", "OpenVR.NET", "NAudio",
        "System.Drawing.Common", "PresentationCore", "PresentationFramework", "WindowsBase"
    ];

    private static readonly string[] DesktopTypes =
    [
        "System.IO.File", "System.IO.Directory", "System.IO.FileStream", "System.Diagnostics.Process",
        "System.Net.Sockets.Socket", "System.Threading.Thread", "System.Reflection.Emit.AssemblyBuilder"
    ];

    internal static void Validate(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader image = new(stream);
        if (!image.HasMetadata)
            throw new InvalidDataException($"Browser game assembly '{Path.GetFileName(assemblyPath)}' has no managed metadata.");

        MetadataReader metadata = image.GetMetadataReader();
        string gameName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        using BrowserGameMetadataResolver resolver = new(assemblyPath, metadata);
        BrowserGameTypeNames names = new(metadata, gameName);
        BrowserGameAuditFindings failures = new();
        HashSet<string> detailedAssemblies = new(StringComparer.Ordinal);

        // TypeRef rows include generic arguments, base types, interface contracts, and signatures.
        foreach (TypeReferenceHandle handle in metadata.TypeReferences)
        {
            BrowserGameTypeName type = names.FromReference(handle);
            if (TypeReason(type, resolver) is not string reason)
                continue;
            detailedAssemblies.Add(type.Assembly);
            failures.Add($"type '{type.FullName}' (assembly '{type.Assembly}': {reason})");
        }

        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            MemberReference member = metadata.GetMemberReference(handle);
            BrowserGameTypeName? owner = names.FromParent(member.Parent);
            if (owner is null)
                continue;
            string memberName = metadata.GetString(member.Name);
            string? reason = TypeReason(owner.Value, resolver)
                ?? resolver.GetMemberReason(metadata, member, owner.Value);
            if (reason is null)
                continue;
            detailedAssemblies.Add(owner.Value.Assembly);
            failures.Add($"member '{owner.Value.FullName}.{memberName}' (assembly '{owner.Value.Assembly}': {reason})");
        }

        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
                continue;
            BrowserGameTypeName owner = names.FromDefinition(method.GetDeclaringType());
            failures.Add($"native import '{owner.FullName}.{metadata.GetString(method.Name)}'");
        }

        if (BrowserGameMetadataResolver.GetPlatformReason(metadata, metadata.GetAssemblyDefinition().GetCustomAttributes())
            is string gameReason)
            failures.Add($"assembly '{gameName}' ({gameReason})");

        foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
        {
            string assembly = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
            string? reason = IsDesktopAssembly(assembly) ? "desktop-only assembly" : resolver.GetAssemblyReason(assembly);
            if (reason is not null && !detailedAssemblies.Contains(assembly))
                failures.Add($"assembly '{assembly}' ({reason}; no referenced type was identified)");
        }

        if (!failures.Any)
            return;
        throw new NotSupportedException($"Browser game '{Path.GetFileName(assemblyPath)}' requires desktop-only APIs: {failures.Render()}. Move platform behavior into the desktop host project.");
    }

    private static string? TypeReason(BrowserGameTypeName type, BrowserGameMetadataResolver resolver)
    {
        if (IsDesktopAssembly(type.Assembly))
            return "desktop-only assembly";
        if (IsFrameworkAssembly(type.Assembly) &&
            (DesktopTypes.Contains(type.FullName, StringComparer.Ordinal) ||
             type.FullName.StartsWith("Microsoft.Win32.Registry", StringComparison.Ordinal)))
            return "desktop-only API";
        return resolver.GetAssemblyReason(type.Assembly) ?? resolver.GetTypeReason(type);
    }

    private static bool IsDesktopAssembly(string assembly) => DesktopAssemblies.Any(blocked =>
        assembly == blocked || assembly.StartsWith(blocked + ".", StringComparison.Ordinal));

    private static bool IsFrameworkAssembly(string assembly) => assembly is "mscorlib" or "netstandard" or "System" or "Microsoft.Win32.Registry"
        || assembly.StartsWith("System.", StringComparison.Ordinal)
        || assembly.StartsWith("Microsoft.Win32.", StringComparison.Ordinal);
}
